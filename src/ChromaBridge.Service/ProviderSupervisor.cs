// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Diagnostics;

namespace ChromaBridge.Service;

/// <summary>
/// Keeps one provider's host process running: starts it, feeds it settings, reads its
/// status, and restarts it with a growing delay when it stops on its own.
/// </summary>
internal sealed class ProviderSupervisor
{
    private const int ExitCannotRun = 3;

    private static readonly TimeSpan s_initialRestartDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_maxRestartDelay = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly string _id;

    private ProviderSettings _settings;
    private Process? _process;
    private ProviderStatus _status;
    private TimeSpan _restartDelay = s_initialRestartDelay;
    private Timer? _restartTimer;
    private bool _stopping;
    private bool _restartRequested;

    public ProviderSupervisor(string id, ProviderSettings settings)
    {
        _id = id;
        _settings = settings.Clone();
        _status = CreateStatus(ProviderState.Disabled, OffMessage);
    }

    public ProviderStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_settings.Enabled)
                StartHost();
        }
    }

    /// <summary>Applies new settings, starting, stopping or restarting the host as needed.</summary>
    public void UpdateSettings(ProviderSettings settings)
    {
        lock (_lock)
        {
            var previous = _settings;
            _settings = settings.Clone();

            if (!_settings.Enabled)
            {
                StopHost();
                _status = CreateStatus(ProviderState.Disabled, OffMessage);
                return;
            }

            // Which devices are driven is decided when the host connects, so a change
            // there needs a fresh host. Everything else applies live.
            if (!previous.Enabled || _process == null)
            {
                StartHost();
            }
            else if (previous.ExclusionsDiffer(_settings))
            {
                // Stopping waits for the host to release its devices, which can take seconds;
                // the caller is answering the tray app, so the restart happens off its thread
                if (!_restartRequested)
                {
                    _restartRequested = true;
                    _status = CreateStatus(ProviderState.Restarting, "Applying the device change...");
                    ThreadPool.QueueUserWorkItem(_ => RestartForSettings());
                }
            }
            else
            {
                SendSettings();
            }
        }
    }

    private void RestartForSettings()
    {
        lock (_lock)
        {
            _restartRequested = false;

            if (_stopping || !_settings.Enabled)
                return;

            Log.Info($"[{_id}] Excluded devices changed; restarting the host");
            StopHost();
            StartHost();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopping = true;
            StopHost();
        }
    }

    private void StartHost()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;

        if (_stopping || _process != null)
            return;

        if (!File.Exists(BridgePaths.HostExePath))
        {
            _status = CreateStatus(ProviderState.Faulted, $"{BridgePaths.HostExeName} is missing from the install folder.");
            Log.Error($"[{_id}] {BridgePaths.HostExePath} not found");
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(BridgePaths.HostExePath)
            {
                ArgumentList = { "--provider", _id },
                WorkingDirectory = BridgePaths.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => OnHostOutput(process, e.Data);
        process.Exited += (_, _) => OnHostExited(process);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"[{_id}] Could not start the host", ex);
            _status = CreateStatus(ProviderState.Faulted, "The host process could not be started.");
            process.Dispose();
            return;
        }

        _process = process;
        _status = CreateStatus(ProviderState.Starting, "Starting...");

        process.BeginOutputReadLine();
        SendSettings();

        Log.Info($"[{_id}] Host started (PID {process.Id})");
    }

    /// <summary>Asks the host to stop, which lets it hand lighting control back, then waits.</summary>
    private void StopHost()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;

        var process = _process;

        if (process == null)
            return;

        _process = null;

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine(BridgeJson.Serialize(new BridgeMessage { Type = MessageTypes.Stop }));
                process.StandardInput.Flush();
                process.StandardInput.Close();

                if (!process.WaitForExit(10000))
                {
                    Log.Warn($"[{_id}] Host did not stop in 10 s; ending it");
                    process.Kill();

                    // Kill() returns before the process is gone; the next host must not start
                    // while this one still holds the vendor SDK
                    process.WaitForExit(2000);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"[{_id}] Failed stopping the host", ex);
        }
        finally
        {
            process.Dispose();
        }

        Log.Info($"[{_id}] Host stopped");
    }

    private void SendSettings()
    {
        try
        {
            _process?.StandardInput.WriteLine(BridgeJson.Serialize(new BridgeMessage { Type = MessageTypes.ProviderSettings, ProviderSettings = _settings }));
            _process?.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log.Error($"[{_id}] Failed sending settings to the host", ex);
        }
    }

    private void OnHostOutput(Process process, string? line)
    {
        var message = BridgeJson.TryParseMessage(line);

        if (message?.Type != MessageTypes.ProviderStatus || message.ProviderStatus == null)
            return;

        lock (_lock)
        {
            if (!ReferenceEquals(process, _process))
                return;

            _status = message.ProviderStatus;

            // A host that reaches Running has recovered; the next restart starts quickly again
            if (_status.State == ProviderState.Running)
                _restartDelay = s_initialRestartDelay;
        }
    }

    private void OnHostExited(Process process)
    {
        // Exited can fire before the last output line has been delivered; waiting without a
        // timeout drains the output, so a final status (the host's reason for stopping) is
        // seen before it is judged below
        try
        {
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            Log.Error($"[{_id}] Could not drain the host's output", ex);
        }

        lock (_lock)
        {
            // Stopped on purpose: StopHost already cleared the field
            if (!ReferenceEquals(process, _process))
                return;

            var exitCode = SafeExitCode(process);

            _process = null;
            process.Dispose();

            if (_stopping)
                return;

            if (exitCode == ExitCannotRun)
            {
                // The host reported why in its last status; keep that message
                if (_status.State != ProviderState.Faulted)
                    _status = CreateStatus(ProviderState.Faulted, "The host cannot run. See the log for details.");

                Log.Error($"[{_id}] Host cannot run (exit code {exitCode}); not restarting");
                return;
            }

            Log.Warn($"[{_id}] Host exited with code {exitCode}; restarting in {_restartDelay.TotalSeconds:F0} s");

            _status = CreateStatus(ProviderState.Restarting, $"Reconnecting in {_restartDelay.TotalSeconds:F0} seconds...");

            _restartTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    if (_settings.Enabled)
                        StartHost();
                }
            }, null, _restartDelay, Timeout.InfiniteTimeSpan);

            _restartDelay = TimeSpan.FromTicks(Math.Min(_restartDelay.Ticks * 2, s_maxRestartDelay.Ticks));
        }
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

    private string OffMessage => _id == KnownProviders.OpenRgb
        ? "Turned off. Turning it on downloads OpenRGB (22 MB) and runs it in the background to reach devices that have no provider of their own."
        : "Turned off.";

    private ProviderStatus CreateStatus(ProviderState state, string message) => new()
    {
        Id = _id,
        DisplayName = KnownProviders.DisplayName(_id),
        State = state,
        Message = message
    };
}
