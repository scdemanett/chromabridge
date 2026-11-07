// Licensed under the MIT license. See LICENSE.txt in the project root.

using ChromaBridge.Chroma;
using ChromaBridge.Providers;
using ChromaBridge.Providers.Asus;
using ChromaBridge.Providers.Corsair;
using ChromaBridge.Providers.OpenRgb;

namespace ChromaBridge.Host;

/// <summary>
/// The provider host: one process per provider, because Razer allows one app ID per process.
/// The service starts it with "--provider &lt;id&gt;", sends settings as JSON lines on standard
/// input, and reads status as JSON lines from standard output. Closing standard input, or a
/// "stop" message, shuts the host down cleanly so lighting control is handed back.
///
/// Exit codes: 0 stopped on request, 2 lost its devices (the service restarts it),
/// 3 cannot run at all (the service does not restart it).
/// </summary>
internal static class Program
{
    private const int ExitStopped = 0;
    private const int ExitDeviceLost = 2;
    private const int ExitCannotRun = 3;

    private static readonly CancellationTokenSource s_stopping = new();
    private static readonly object s_outputLock = new();
    private static readonly ManualResetEventSlim s_settingsReceived = new(false);

    private static volatile ProviderSettings s_settings = new();
    private static volatile ColorCorrection s_correction = ColorCorrection.Identity;
    private static volatile DeviceWorker[] s_workers = [];
    private static volatile List<DeviceStatus> s_excludedDevices = [];
    private static volatile string s_message = "Starting...";
    private static volatile bool s_chromaLive;
    private static volatile bool s_deviceLost;

    private static ProviderState s_state = ProviderState.Starting;
    private static long s_framesReceived;
    private static string s_providerId = "";

    private static int Main(string[] args)
    {
        s_providerId = ReadProviderId(args);

        Log.Initialize("host-" + (s_providerId.Length > 0 ? s_providerId : "unknown"));
        Log.Info($"ChromaBridge host v{ProcessInfo.Version} starting for provider '{s_providerId}' (elevated: {ProcessInfo.IsElevated})");

        using var provider = CreateProvider(s_providerId);

        if (provider == null)
        {
            Log.Error($"Unknown provider '{s_providerId}'");
            Fault("This provider is not available in this build yet.");
            return ExitCannotRun;
        }

        // "--standalone" runs without a service, for testing: default settings, no input
        if (args.Contains("--standalone", StringComparer.OrdinalIgnoreCase))
        {
            s_settings = new BridgeSettings().For(s_providerId);
            s_correction = new ColorCorrection(s_settings);
        }
        else
        {
            var input = new Thread(ReadInput) { IsBackground = true, Name = "Input" };
            input.Start();

            // The service sends the settings first; they decide which devices are excluded
            s_settingsReceived.Wait(TimeSpan.FromSeconds(5), s_stopping.Token);
        }

        using var statusTimer = new Timer(_ => WriteStatus(), null, 0, 1000);

        ChromaBroadcastClient? chroma = null;

        try
        {
            s_state = ProviderState.WaitingForDevices;

            provider.Configure(s_settings);

            var devices = provider.Connect(message => s_message = message, s_stopping.Token);

            s_workers = CreateWorkers(devices);

            foreach (var worker in s_workers)
                worker.Start(s_stopping.Token);

            chroma = new ChromaBroadcastClient();
            chroma.FrameReceived += OnFrame;
            chroma.LiveChanged += live =>
            {
                s_chromaLive = live;
                Log.Info(live ? "Razer Chroma Broadcast connected" : "Razer Chroma Broadcast disconnected");
            };

            chroma.Start(AppIds.Get(s_providerId));

            s_state = ProviderState.Running;
            s_message = s_workers.Length == 0 ? "Every device is excluded." : "Following Razer Chroma.";

            Log.Info($"Running with {s_workers.Length} device(s)");

            s_stopping.Token.WaitHandle.WaitOne();
        }
        catch (OperationCanceledException) when (s_stopping.IsCancellationRequested)
        {
        }
        catch (ChromaBroadcastException ex)
        {
            Log.Error("Could not connect to Razer Chroma Broadcast", ex);
            Shutdown(chroma, provider);
            Fault(ex.Message);
            return ExitCannotRun;
        }
        catch (Exception ex)
        {
            Log.Error("The host failed", ex);
            Shutdown(chroma, provider);
            return ExitDeviceLost;
        }

        Shutdown(chroma, provider);

        Log.Info(s_deviceLost ? "Stopped: a device was lost" : "Stopped");

        return s_deviceLost ? ExitDeviceLost : ExitStopped;
    }

    private static string ReadProviderId(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase))
                return args[i + 1].ToLowerInvariant();
        }

        return "";
    }

    private static ILightingProvider? CreateProvider(string id) => id switch
    {
        KnownProviders.Asus => new AsusProvider(),
        KnownProviders.Corsair => new CorsairProvider(),
        KnownProviders.OpenRgb => new OpenRgbProvider(),
        _ => null
    };

    private static DeviceWorker[] CreateWorkers(IReadOnlyList<ILightingDevice> devices)
    {
        var settings = s_settings;
        var workers = new List<DeviceWorker>();
        var excluded = new List<DeviceStatus>();

        foreach (var device in devices)
        {
            if (settings.IsExcluded(device.Name))
            {
                Log.Info($"Excluded in settings: \"{device.Name}\"");
                excluded.Add(new DeviceStatus { Name = device.Name, Lights = device.LightCount, Excluded = true });
                continue;
            }

            var name = device.Name;

            // Read through s_settings on every frame so a mapping change applies at once
            workers.Add(new DeviceWorker(device, () => s_settings.MappingFor(name), () => s_correction, OnDeviceLost));
        }

        s_excludedDevices = excluded;

        return [.. workers];
    }

    private static void OnFrame(ChromaFrame frame)
    {
        var first = Interlocked.Increment(ref s_framesReceived) == 1;

        if (first)
            Log.Info($"First Chroma frame: {frame}");

        if (first || !IsBlack(frame))
            Volatile.Write(ref s_lastColorStamp, Environment.TickCount64);

        foreach (var worker in s_workers)
            worker.Post(frame);
    }

    private static void OnDeviceLost()
    {
        s_deviceLost = true;
        s_stopping.Cancel();
    }

    private static void Shutdown(ChromaBroadcastClient? chroma, ILightingProvider provider)
    {
        chroma?.Dispose();

        // Wait so no worker is mid-write when control is released
        s_stopping.Cancel();

        foreach (var worker in s_workers)
            worker.Join();

        try
        {
            provider.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Error("Failed releasing the provider", ex);
        }
    }

    /// <summary>Reads messages from the service until it asks for a stop or goes away.</summary>
    private static void ReadInput()
    {
        try
        {
            string? line;

            while ((line = Console.In.ReadLine()) != null)
            {
                var message = BridgeJson.TryParseMessage(line);

                if (message == null)
                    continue;

                if (message.Type == MessageTypes.Stop)
                    break;

                if (message.Type == MessageTypes.ProviderSettings && message.ProviderSettings != null)
                {
                    s_settings = message.ProviderSettings;
                    s_correction = new ColorCorrection(message.ProviderSettings);

                    s_settingsReceived.Set();

                    Log.Info($"Settings: gamma {s_correction.Gamma}, saturation {s_correction.Saturation}, brightness {s_correction.Brightness}, {message.ProviderSettings.ExcludedDevices.Count} excluded device(s)");
                }
            }
        }
        catch (IOException)
        {
        }

        // Standard input closing means the service is gone: stop and hand control back
        var wasRunning = s_state == ProviderState.Running;

        s_stopping.Cancel();

        // While still looking for devices the main thread can be inside a vendor call that
        // takes up to a minute and cannot be cancelled. Nothing is held yet, so just leave.
        if (!wasRunning)
        {
            Thread.Sleep(2000);
            Log.Info("Stopped while waiting for devices");
            Environment.Exit(ExitStopped);
        }
    }

    private static long s_lastFrames;
    private static long s_lastFrameStamp = Environment.TickCount64;

    /// <summary>When a frame with any color in it last arrived; set by the first frame too.</summary>
    private static long s_lastColorStamp;

    /// <summary>Black for this long, while frames keep arriving, means the tile is in no Studio layer.</summary>
    private const long OnlyBlackAfterMs = 5000;

    private static bool IsBlack(ChromaFrame frame)
    {
        for (var i = 0; i < ChromaFrame.ColorCount; i++)
        {
            if (frame[i].ToColorRef() != 0)
                return false;
        }

        return true;
    }

    private static void WriteStatus()
    {
        try
        {
            var frames = Interlocked.Read(ref s_framesReceived);
            var now = Environment.TickCount64;
            var seconds = Math.Max((now - s_lastFrameStamp) / 1000.0, 0.001);

            var status = new ProviderStatus
            {
                Id = s_providerId,
                DisplayName = KnownProviders.DisplayName(s_providerId),
                State = s_state,
                Message = s_message,
                ChromaConnected = s_chromaLive,
                FramesPerSecond = Math.Round((frames - s_lastFrames) / seconds, 1),
                OnlyBlack = s_chromaLive && frames > s_lastFrames && now - Volatile.Read(ref s_lastColorStamp) >= OnlyBlackAfterMs,
                Elevated = ProcessInfo.IsElevated
            };

            s_lastFrames = frames;
            s_lastFrameStamp = now;

            foreach (var worker in s_workers)
                status.Devices.Add(worker.TakeStatus());

            status.Devices.AddRange(s_excludedDevices);

            WriteMessage(new BridgeMessage { Type = MessageTypes.ProviderStatus, ProviderStatus = status });
        }
        catch (Exception ex)
        {
            Log.Error("Failed reporting status", ex);
        }
    }

    private static void Fault(string message)
    {
        WriteMessage(new BridgeMessage
        {
            Type = MessageTypes.ProviderStatus,
            ProviderStatus = new ProviderStatus
            {
                Id = s_providerId,
                DisplayName = KnownProviders.DisplayName(s_providerId),
                State = ProviderState.Faulted,
                Message = message,
                Elevated = ProcessInfo.IsElevated
            }
        });
    }

    private static void WriteMessage(BridgeMessage message)
    {
        lock (s_outputLock)
        {
            try
            {
                Console.Out.WriteLine(BridgeJson.Serialize(message));
                Console.Out.Flush();
            }
            catch (IOException)
            {
            }
        }
    }
}
