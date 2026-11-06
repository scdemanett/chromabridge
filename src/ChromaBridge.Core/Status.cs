// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

public enum ProviderState
{
    /// <summary>Turned off in the settings.</summary>
    Disabled,

    /// <summary>The host process is starting.</summary>
    Starting,

    /// <summary>The host is running but the vendor software has not reported any devices yet.</summary>
    WaitingForDevices,

    /// <summary>Devices are being driven from Chroma.</summary>
    Running,

    /// <summary>The host stopped and will be restarted.</summary>
    Restarting,

    /// <summary>The host cannot run; see the message.</summary>
    Faulted
}

/// <summary>
/// The state of one device.
/// </summary>
public sealed class DeviceStatus
{
    public string Name { get; set; } = "";

    public int Lights { get; set; }

    public bool Excluded { get; set; }

    /// <summary>How often the device was written in the last second.</summary>
    public double UpdatesPerSecond { get; set; }

    /// <summary>The average time one write took over the last second, in milliseconds.</summary>
    public double ApplyMilliseconds { get; set; }

    public long Failures { get; set; }
}

/// <summary>
/// The state of one provider.
/// </summary>
public sealed class ProviderStatus
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public ProviderState State { get; set; }

    /// <summary>What the provider is doing or why it is not running, in plain words.</summary>
    public string Message { get; set; } = "";

    /// <summary>True once Razer Chroma reports the connection as live.</summary>
    public bool ChromaConnected { get; set; }

    /// <summary>Frames received from Razer in the last second.</summary>
    public double FramesPerSecond { get; set; }

    public bool Elevated { get; set; }

    public List<DeviceStatus> Devices { get; set; } = [];
}

/// <summary>
/// The state of the whole bridge, as the service reports it to the tray app.
/// </summary>
public sealed class BridgeStatus
{
    public string Version { get; set; } = "";

    public bool Elevated { get; set; }

    /// <summary>True when the scheduled task that starts the helper at login exists.</summary>
    public bool StartsAtLogin { get; set; }

    public List<ProviderStatus> Providers { get; set; } = [];
}
