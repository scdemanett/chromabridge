// Licensed under the MIT license. See LICENSE.txt in the project root.

using ChromaBridge.Service.OpenRgb;
using ChromaBridge.Service.Updates;

namespace ChromaBridge.Service;

/// <summary>
/// The ChromaBridge helper. It has no window: it starts one host process per enabled
/// provider, keeps them running, owns the settings file, and answers the tray app over a
/// named pipe. Lighting keeps working when the tray app is closed.
///
/// It needs administrator rights for ASUS (the host processes inherit them), so it is meant
/// to be started by a logon scheduled task that runs with highest privileges.
///
/// Switches:
///   --stop                          asks a running service to shut down cleanly
///   --register-startup [--user SID] creates the logon task (needs administrator rights)
///   --unregister-startup            removes the logon task (needs administrator rights)
/// </summary>
internal static class Program
{
    private static readonly object s_settingsLock = new();
    private static readonly Dictionary<string, ProviderSupervisor> s_supervisors = new(StringComparer.OrdinalIgnoreCase);

    private static readonly OpenRgbManager s_openRgb = new();
    private static readonly UpdateManager s_updates = new();

    private static BridgeSettings s_settings = new();
    private static bool s_startsAtLogin;
    private static long s_startsAtLoginChecked;

    private static int Main(string[] args)
    {
        if (args.Contains("--stop", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(BridgePaths.ServiceStopEventName, out var existing))
            {
                using (existing)
                    existing.Set();
            }

            return 0;
        }

        if (args.Contains("--register-startup", StringComparer.OrdinalIgnoreCase))
        {
            Log.Initialize("setup");
            DataDirectory.Secure();

            return StartupTask.Register(ReadOption(args, "--user")) ? 0 : 1;
        }

        if (args.Contains("--unregister-startup", StringComparer.OrdinalIgnoreCase))
        {
            Log.Initialize("setup");

            return StartupTask.Unregister() ? 0 : 1;
        }

        using var mutex = new Mutex(true, BridgePaths.ServiceMutexName, out var first);

        if (!first)
            return 1;

        DataDirectory.Secure();
        Log.Initialize("service");
        Log.Info($"ChromaBridge service v{ProcessInfo.Version} starting (elevated: {ProcessInfo.IsElevated})");

        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, BridgePaths.ServiceStopEventName);
        using var stopping = new CancellationTokenSource();

        s_settings = SettingsStore.Load();

        foreach (var (id, _) in KnownProviders.All)
            s_supervisors[id] = new ProviderSupervisor(id, s_settings.For(id));

        foreach (var supervisor in s_supervisors.Values)
            supervisor.Start();

        ConfigureOpenRgb(s_settings);
        s_openRgb.Start(stopping.Token);

        UpdateManager.RelaunchTrayAfterUpdate();
        s_updates.SetAutomatic(s_settings.CheckForUpdates);
        s_updates.Start(stopping.Token);

        new ControlPipeServer(Handle).Start(stopping.Token);

        Log.Info("Ready");

        stopEvent.WaitOne();

        Log.Info("Stopping...");

        stopping.Cancel();

        // Stop the hosts together: each one waits for its own devices to be released
        Task.WaitAll([.. s_supervisors.Values.Select(x => Task.Run(x.Stop))], TimeSpan.FromSeconds(15));

        // After the hosts, so the OpenRGB provider is not still writing to it
        s_openRgb.Stop();

        Log.Info("Stopped");

        return 0;
    }

    private static BridgeMessage Handle(BridgeMessage request)
    {
        switch (request.Type)
        {
            case MessageTypes.GetStatus:
                return new BridgeMessage { Type = MessageTypes.Status, Status = CreateStatus() };

            case MessageTypes.GetSettings:
                lock (s_settingsLock)
                    return new BridgeMessage { Type = MessageTypes.Settings, Settings = s_settings.Clone() };

            case MessageTypes.SetSettings when request.Settings != null:
                return ApplySettings(request.Settings);

            case MessageTypes.CheckForUpdates:
                s_updates.RequestCheck();
                return new BridgeMessage { Type = MessageTypes.Status, Status = CreateStatus() };

            // The client only asks; what gets installed is decided and verified here
            case MessageTypes.InstallUpdate:
                s_updates.RequestInstall();
                return new BridgeMessage { Type = MessageTypes.Status, Status = CreateStatus() };

            default:
                return new BridgeMessage { Type = MessageTypes.Error, Error = $"Unknown request '{request.Type}'." };
        }
    }

    private static BridgeMessage ApplySettings(BridgeSettings settings)
    {
        lock (s_settingsLock)
        {
            s_settings = settings.Normalize();

            try
            {
                SettingsStore.Save(s_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not save the settings file", ex);
            }

            foreach (var (id, supervisor) in s_supervisors)
                supervisor.UpdateSettings(s_settings.For(id));

            ConfigureOpenRgb(s_settings);
            s_updates.SetAutomatic(s_settings.CheckForUpdates);

            return new BridgeMessage { Type = MessageTypes.Settings, Settings = s_settings.Clone() };
        }
    }

    private static void ConfigureOpenRgb(BridgeSettings settings)
    {
        var openRgb = settings.For(KnownProviders.OpenRgb);

        s_openRgb.Configure(openRgb.Enabled, openRgb.SkipVendors);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    /// <summary>Asks Task Scheduler at most every five seconds; the tray app polls every second.</summary>
    private static bool StartsAtLogin()
    {
        var now = Environment.TickCount64;

        if (s_startsAtLoginChecked == 0 || now - s_startsAtLoginChecked > 5000)
        {
            s_startsAtLogin = StartupTask.IsRegistered();
            s_startsAtLoginChecked = now;
        }

        return s_startsAtLogin;
    }

    private static BridgeStatus CreateStatus()
    {
        var status = new BridgeStatus { Version = ProcessInfo.Version, Elevated = ProcessInfo.IsElevated, StartsAtLogin = StartsAtLogin(), Update = s_updates.Status };

        foreach (var (id, _) in KnownProviders.All)
        {
            var providerStatus = s_supervisors[id].Status;

            // While OpenRGB itself is being downloaded or started, say that instead of the
            // provider's "waiting for OpenRGB"
            if (id == KnownProviders.OpenRgb && providerStatus.State != ProviderState.Running && s_openRgb.Message is { } message)
            {
                providerStatus = new ProviderStatus
                {
                    Id = providerStatus.Id,
                    DisplayName = providerStatus.DisplayName,
                    State = providerStatus.State,
                    Message = message,
                    Elevated = providerStatus.Elevated,
                    Devices = providerStatus.Devices
                };
            }

            status.Providers.Add(providerStatus);
        }

        return status;
    }
}
