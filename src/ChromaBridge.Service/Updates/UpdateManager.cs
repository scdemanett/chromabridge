// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChromaBridge.Service.OpenRgb;

namespace ChromaBridge.Service.Updates;

/// <summary>
/// Finds new ChromaBridge releases on GitHub and installs one when the user asks.
///
/// - Checking is automatic (once a day) unless the user turns it off, and can be asked for.
/// - Nothing is downloaded or installed until the user asks for it.
/// - The installer comes only from this project's own releases, is kept in the
///   administrator-only data folder, and runs only if <see cref="AuthenticodeVerifier"/>
///   accepts its signature. A client of the control pipe can ask for the install but cannot
///   say what gets installed.
/// </summary>
internal sealed class UpdateManager
{
    private const long MaxInstallerBytes = 500L * 1024 * 1024;

    private static readonly TimeSpan s_firstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_checkInterval = TimeSpan.FromHours(24);

    private static readonly string s_updatesDirectory = Path.Combine(BridgePaths.DataDirectory, "updates");

    /// <summary>Left for the next service start: the tray app should be started again after an update.</summary>
    private static readonly string s_relaunchMarker = Path.Combine(s_updatesDirectory, "relaunch-tray");

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);

    private bool _automatic = true;
    private bool _checkRequested;
    private bool _installRequested;
    private DateTime _lastCheck = DateTime.MinValue;
    private UpdateStatus _status = new();
    private string? _installerUrl;
    private string? _installerName;

    public UpdateStatus Status
    {
        get
        {
            lock (_lock)
                return _status.Clone();
        }
    }

    public void Start(CancellationToken stoppingToken)
    {
        var thread = new Thread(() => Run(stoppingToken)) { IsBackground = true, Name = "Updates" };

        thread.Start();
    }

    public void SetAutomatic(bool automatic)
    {
        lock (_lock)
            _automatic = automatic;
    }

    public void RequestCheck()
    {
        lock (_lock)
            _checkRequested = true;

        _wake.Set();
    }

    public void RequestInstall()
    {
        lock (_lock)
            _installRequested = true;

        _wake.Set();
    }

    /// <summary>
    /// After an update the installer restarts the service but not the tray app, because it
    /// ran with no user interface. Start the tray app again, as the logged-in user.
    /// </summary>
    public static void RelaunchTrayAfterUpdate()
    {
        try
        {
            if (!File.Exists(s_relaunchMarker))
                return;

            File.Delete(s_relaunchMarker);

            var appPath = Path.Combine(BridgePaths.InstallDirectory, BridgePaths.AppExeName);

            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(BridgePaths.AppExeName)).Length == 0 && File.Exists(appPath))
            {
                UnelevatedLauncher.Start(appPath, "--tray", BridgePaths.InstallDirectory).Dispose();
                Log.Info("[update] Started the tray app again after the update");
            }
        }
        catch (Exception ex)
        {
            Log.Error("[update] Could not start the tray app after the update", ex);
        }
    }

    private void Run(CancellationToken stoppingToken)
    {
        var handles = new[] { _wake, stoppingToken.WaitHandle };
        var started = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool check, install;

            lock (_lock)
            {
                var due = _automatic
                    && DateTime.UtcNow - started >= s_firstCheckDelay
                    && DateTime.UtcNow - _lastCheck >= s_checkInterval;

                check = _checkRequested || due;
                install = _installRequested;

                _checkRequested = false;
                _installRequested = false;
            }

            try
            {
                if (check || install)
                    DeleteOldInstallers();

                // An install always works from a fresh check, so it never uses a stale address
                if (check || install)
                    Check(stoppingToken);

                if (install)
                    Install(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("[update] The update step failed", ex);
                SetState(UpdateState.Failed, "The update could not be completed. See the service log.");
            }

            WaitHandle.WaitAny(handles, TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Removes installers left from earlier updates. By the time a check runs, the installer
    /// that put this version in place has long exited, so its file is free to delete.
    /// </summary>
    private static void DeleteOldInstallers()
    {
        try
        {
            if (!Directory.Exists(s_updatesDirectory))
                return;

            foreach (var file in Directory.GetFiles(s_updatesDirectory, "*-setup.exe"))
            {
                File.Delete(file);
                Log.Info($"[update] Deleted the old installer {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("[update] Could not delete an old installer", ex);
        }
    }

    private void Check(CancellationToken stoppingToken)
    {
        SetState(UpdateState.Checking, "Checking for updates...");

        lock (_lock)
            _lastCheck = DateTime.UtcNow;

        try
        {
            using var http = CreateClient();
            using var response = http.GetAsync($"https://api.github.com/repos/{BridgePaths.UpdateRepository}/releases/latest", stoppingToken).GetAwaiter().GetResult();

            // No release published yet (or the repository does not exist yet)
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetState(UpdateState.UpToDate, "No newer version is available.", checkedNow: true);
                return;
            }

            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync(stoppingToken).GetAwaiter().GetResult());

            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                SetState(UpdateState.Failed, $"The latest release has a version that could not be read (\"{tag}\").", checkedNow: true);
                return;
            }

            var current = Version.Parse(ProcessInfo.Version);

            if (latest <= current)
            {
                SetState(UpdateState.UpToDate, "ChromaBridge is up to date.", checkedNow: true);
                return;
            }

            string? url = null, name = null;

            if (root.TryGetProperty("assets", out var assets))
            {
                // The installer must come from this project's own release downloads, and it
                // should be this copy's edition; a release with one unnamed installer (before
                // the Full and Lite split) is accepted too
                var prefix = $"https://github.com/{BridgePaths.UpdateRepository}/releases/download/";
                var wanted = $"-{BridgePaths.Edition}-setup.exe";

                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

                    if (!assetName.StartsWith("ChromaBridge-", StringComparison.OrdinalIgnoreCase)
                        || !assetName.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
                        || assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || !assetUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var edition = assetName.EndsWith(wanted, StringComparison.OrdinalIgnoreCase);
                    var unnamed = !assetName.EndsWith("-full-setup.exe", StringComparison.OrdinalIgnoreCase)
                        && !assetName.EndsWith("-lite-setup.exe", StringComparison.OrdinalIgnoreCase);

                    if (edition || (unnamed && url == null))
                    {
                        url = assetUrl;
                        name = assetName;

                        if (edition)
                            break;
                    }
                }
            }

            if (url == null)
            {
                SetState(UpdateState.Failed, $"Version {latest.ToString(3)} was released, but it has no installer attached yet.", checkedNow: true);
                return;
            }

            lock (_lock)
            {
                _installerUrl = url;
                _installerName = name;

                _status = new UpdateStatus
                {
                    State = UpdateState.Available,
                    LatestVersion = latest.ToString(3),
                    Message = $"Version {latest.ToString(3)} is available.",
                    ReleaseUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : null,
                    LastChecked = DateTime.UtcNow
                };
            }

            Log.Info($"[update] Version {latest.ToString(3)} is available ({name})");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
        {
            Log.Error("[update] Checking for updates failed", ex);
            SetState(UpdateState.Failed, "Could not check for updates. ChromaBridge will try again later.", checkedNow: true);
        }
    }

    private void Install(CancellationToken stoppingToken)
    {
        string? url, name, version;

        lock (_lock)
        {
            if (_status.State != UpdateState.Available)
                return;

            url = _installerUrl;
            name = _installerName;
            version = _status.LatestVersion;
        }

        if (url == null || name == null)
            return;

        if (!ProcessInfo.IsElevated)
        {
            SetState(UpdateState.Failed, "The helper is not running as administrator, so it cannot install updates.", keepVersion: true);
            return;
        }

        Directory.CreateDirectory(s_updatesDirectory);

        var path = Path.Combine(s_updatesDirectory, name);

        try
        {
            SetState(UpdateState.Downloading, $"Downloading version {version}...", keepVersion: true);
            Log.Info($"[update] Downloading {url}");

            using (var http = CreateClient())
            using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stoppingToken).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength > MaxInstallerBytes)
                    throw new InvalidDataException("The installer is implausibly large.");

                using var file = File.Create(path);
                using var body = response.Content.ReadAsStream(stoppingToken);

                // The length header can be absent or wrong, so the bytes are counted as they arrive
                var buffer = new byte[81920];
                long total = 0;
                int read;

                while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;

                    if (total > MaxInstallerBytes)
                        throw new InvalidDataException("The installer is implausibly large.");

                    file.Write(buffer, 0, read);
                    stoppingToken.ThrowIfCancellationRequested();
                }
            }

            SetState(UpdateState.Verifying, "Checking the installer's signature...", keepVersion: true);

            var refusal = AuthenticodeVerifier.Verify(path);

            if (refusal != null)
            {
                File.Delete(path);
                Log.Error($"[update] The installer was refused and deleted: {refusal}");
                SetState(UpdateState.Failed, "The downloaded installer is not a genuine ChromaBridge release, so it was not run.", keepVersion: true);
                return;
            }

            Log.Info("[update] The installer's signature is genuine; starting it");
            SetState(UpdateState.Installing, $"Installing version {version}. Lighting pauses for a few seconds.", keepVersion: true);

            File.WriteAllText(s_relaunchMarker, version ?? "");

            // The installer stops this service, replaces the files and starts it again
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = false,
                WorkingDirectory = s_updatesDirectory
            })?.Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Log.Error("[update] Downloading or starting the installer failed", ex);
            SetState(UpdateState.Failed, "The update could not be downloaded. Try again later.", keepVersion: true);
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub's API refuses requests with no user agent
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(BridgePaths.ProductName, ProcessInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return http;
    }

    private void SetState(UpdateState state, string message, bool checkedNow = false, bool keepVersion = false)
    {
        lock (_lock)
        {
            _status = new UpdateStatus
            {
                State = state,
                Message = message,
                LatestVersion = keepVersion ? _status.LatestVersion : null,
                ReleaseUrl = keepVersion ? _status.ReleaseUrl : null,
                LastChecked = checkedNow ? DateTime.UtcNow : _status.LastChecked
            };
        }
    }
}
