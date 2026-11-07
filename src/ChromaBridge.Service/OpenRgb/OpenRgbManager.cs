// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChromaBridge.Service.OpenRgb;

/// <summary>
/// Runs OpenRGB for the OpenRGB provider, so the user does not have to install or start it.
///
/// What it does, and the limits it keeps:
/// - Downloads one pinned OpenRGB release from OpenRGB's official release host the first
///   time the provider is turned on, and only uses it if its SHA-256 matches.
/// - Keeps the program in the administrator-only data folder, so it cannot be swapped out.
/// - Starts it as the logged-in user without administrator rights, with no window, serving
///   its SDK on this PC only.
/// - Writes its settings so detectors for vendors that ChromaBridge drives natively are off.
/// - Never installs PawnIO, so OpenRGB cannot reach memory or motherboard buses.
/// - Leaves an OpenRGB the user runs themselves alone: if something already serves the SDK
///   port, that one is used and nothing is started.
/// </summary>
internal sealed class OpenRgbManager
{
    private static readonly string Version = Bundles.OpenRgb.Version;
    private const string ExeName = "OpenRGB.exe";
    private const int SdkPort = 6742;

    /// <summary>
    /// Detectors that are always off, whatever vendors are skipped. "HID LampArray Device"
    /// claims any device that speaks the generic LampArray standard, whoever made it, so a
    /// vendor rule cannot keep it away from devices their own software drives (it picked up
    /// a Razer keyboard that Synapse owns).
    /// </summary>
    private static readonly string[] s_alwaysOff = ["HID LampArray Device"];

    private static readonly TimeSpan s_initialRestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_maxRestartDelay = TimeSpan.FromMinutes(2);

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);

    private bool _wanted;
    private string[] _skipVendors = [];
    private string[] _runningWithSkipVendors = [];
    private Process? _process;
    private TimeSpan _restartDelay = s_initialRestartDelay;
    private DateTime _notBefore = DateTime.MinValue;
    private volatile string? _message;

    /// <summary>Where the pinned OpenRGB release is unpacked. Administrators only may write here.</summary>
    private static string InstallDirectory => Path.Combine(BridgePaths.DataDirectory, "openrgb", Version);

    /// <summary>OpenRGB writes its settings and logs here as the logged-in user.</summary>
    private static string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BridgePaths.ProductName, "OpenRGB");

    /// <summary>What the manager is doing or why OpenRGB is not running; null when all is well.</summary>
    public string? Message => _message;

    public void Start(CancellationToken stoppingToken)
    {
        var thread = new Thread(() => Run(stoppingToken)) { IsBackground = true, Name = "OpenRGB Manager" };

        thread.Start();
    }

    /// <summary>Says whether OpenRGB should be running and which vendors it must leave alone.</summary>
    public void Configure(bool wanted, IEnumerable<string> skipVendors)
    {
        lock (_lock)
        {
            _wanted = wanted;
            _skipVendors = [.. skipVendors.Order(StringComparer.OrdinalIgnoreCase)];
        }

        _wake.Set();
    }

    /// <summary>Stops the OpenRGB this manager started. Called when the service shuts down.</summary>
    public void Stop() => StopProcess();

    private void Run(CancellationToken stoppingToken)
    {
        var handles = new[] { _wake, stoppingToken.WaitHandle };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Reconcile(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error("[openrgb] Managing OpenRGB failed", ex);
                Fail("ChromaBridge could not start OpenRGB. See the service log.");
            }

            WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(3));
        }
    }

    private void Reconcile(CancellationToken stoppingToken)
    {
        bool wanted;
        string[] skipVendors;

        lock (_lock)
        {
            wanted = _wanted;
            skipVendors = _skipVendors;
        }

        if (!wanted)
        {
            StopProcess();
            _message = null;
            return;
        }

        if (_process is { HasExited: false })
        {
            // The vendors to skip changed: restart so OpenRGB lets go of (or picks up) them
            if (!skipVendors.SequenceEqual(_runningWithSkipVendors, StringComparer.OrdinalIgnoreCase))
            {
                Log.Info("[openrgb] The vendors to leave alone changed; restarting OpenRGB");
                StopProcess();
            }
            else
            {
                _message = null;
                return;
            }
        }
        else if (_process != null)
        {
            Log.Warn($"[openrgb] OpenRGB exited with code {SafeExitCode(_process)}; restarting in {_restartDelay.TotalSeconds:F0} s");

            _process.Dispose();
            _process = null;
            _notBefore = DateTime.UtcNow + _restartDelay;
            _restartDelay = TimeSpan.FromTicks(Math.Min(_restartDelay.Ticks * 2, s_maxRestartDelay.Ticks));
        }

        if (DateTime.UtcNow < _notBefore)
            return;

        // An OpenRGB the user started themselves: use it and stay out of its way
        if (IsSdkServerListening())
        {
            _message = null;
            return;
        }

        if (!EnsureInstalled(stoppingToken))
            return;

        WriteSettings(skipVendors);

        var exePath = Path.Combine(InstallDirectory, ExeName);

        try
        {
            _message = "Starting OpenRGB...";

            // "--server" alone runs OpenRGB with no window; the SDK listens on 127.0.0.1 only
            _process = UnelevatedLauncher.Start(exePath, $"--server --config \"{ConfigDirectory}\"", InstallDirectory);
            _runningWithSkipVendors = skipVendors;

            Log.Info($"[openrgb] Started OpenRGB {Version} unelevated (PID {_process.Id})");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("[openrgb] Could not start OpenRGB as the logged-in user", ex);
            Fail("OpenRGB could not be started as your user account. See the service log.");
        }
    }

    private bool EnsureInstalled(CancellationToken stoppingToken)
    {
        var exePath = Path.Combine(InstallDirectory, ExeName);

        if (File.Exists(exePath))
            return true;

        // Only an elevated service may write the install folder; unelevated development runs
        // would put the program where any process could replace it
        var stagingDirectory = InstallDirectory + ".staging";

        try
        {
            // The Full installer ships the archive; otherwise it is downloaded and checked
            var zipPath = BundleStore.Obtain(Bundles.OpenRgb, text => _message = text, stoppingToken);

            if (zipPath == null)
            {
                if (stoppingToken.IsCancellationRequested)
                    return false;

                Fail("OpenRGB could not be downloaded. ChromaBridge will try again.", TimeSpan.FromMinutes(2));
                return false;
            }

            _message = "Unpacking OpenRGB...";

            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);

            ExtractFlattened(zipPath, stagingDirectory);

            if (!File.Exists(Path.Combine(stagingDirectory, ExeName)))
                throw new InvalidDataException($"The OpenRGB archive does not contain {ExeName}.");

            // Move into place in one step so a half-unpacked folder is never used
            Directory.Move(stagingDirectory, InstallDirectory);

            Log.Info($"[openrgb] Installed OpenRGB {Version} to {InstallDirectory}");

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("[openrgb] Unpacking OpenRGB failed", ex);
            Fail("OpenRGB could not be unpacked. ChromaBridge will try again.", TimeSpan.FromMinutes(2));
            return false;
        }
    }

    /// <summary>
    /// Unpacks the archive, dropping its single top-level folder. Entries that would land
    /// outside the target folder are refused.
    /// </summary>
    private static void ExtractFlattened(string zipPath, string targetDirectory)
    {
        var root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(root);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            var separator = entry.FullName.IndexOf('/');
            var relative = separator >= 0 ? entry.FullName[(separator + 1)..] : entry.FullName;

            if (relative.Length == 0)
                continue;

            var destination = Path.GetFullPath(Path.Combine(root, relative));

            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The OpenRGB archive has an entry outside its folder: {entry.FullName}");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Writes which detectors OpenRGB may run. Detectors for a vendor in
    /// <paramref name="skipVendors"/> are off, because ChromaBridge drives that vendor's
    /// devices through the vendor's own software and two writers on one device fight.
    /// Everything else OpenRGB has written to its settings file is kept.
    /// </summary>
    private static void WriteSettings(string[] skipVendors)
    {
        Directory.CreateDirectory(ConfigDirectory);

        var path = Path.Combine(ConfigDirectory, "OpenRGB.json");
        JsonObject settings;

        try
        {
            settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        var detectorSettings = settings["Detectors"] as JsonObject ?? [];
        var detectors = detectorSettings["detectors"] as JsonObject ?? [];

        // The names from the pinned release, plus any OpenRGB added to the file by itself
        var names = new HashSet<string>(ReadDetectorNames(), StringComparer.Ordinal);

        foreach (var existing in detectors)
            names.Add(existing.Key);

        var updated = new JsonObject();
        var skipped = 0;

        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var skip = s_alwaysOff.Contains(name, StringComparer.OrdinalIgnoreCase)
                || skipVendors.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

            if (skip)
                skipped++;

            updated[name] = !skip;
        }

        detectorSettings["detectors"] = updated;
        settings["Detectors"] = detectorSettings;

        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Log.Info($"[openrgb] Wrote OpenRGB settings: {names.Count} detectors, {skipped} off for {(skipVendors.Length == 0 ? "no vendors" : string.Join(", ", skipVendors))}");
    }

    private static IEnumerable<string> ReadDetectorNames()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ChromaBridge.Service.OpenRgb.Detectors-{Version}.txt");

        if (stream == null)
            yield break;

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                yield return line;
        }
    }

    private static bool IsSdkServerListening()
    {
        try
        {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", SdkPort).Wait(TimeSpan.FromMilliseconds(400)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    private void StopProcess()
    {
        var process = _process;

        if (process == null)
            return;

        _process = null;

        try
        {
            if (!process.HasExited)
            {
                // OpenRGB has no window in server mode, so there is nothing to ask politely
                process.Kill();
                process.WaitForExit(5000);

                Log.Info("[openrgb] Stopped OpenRGB");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Error("[openrgb] Failed stopping OpenRGB", ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Fail(string message, TimeSpan? retryAfter = null)
    {
        _message = message;
        _notBefore = DateTime.UtcNow + (retryAfter ?? TimeSpan.FromSeconds(30));
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
