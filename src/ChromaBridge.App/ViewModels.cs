// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChromaBridge.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
/// One entry in the zone mapping list.
/// </summary>
public sealed record MappingOption(ZoneMapping Mapping, string DisplayName)
{
    public static IReadOnlyList<MappingOption> All { get; } = [.. ZoneMapper.All.Select(x => new MappingOption(x.Mapping, x.DisplayName))];
}

/// <summary>
/// One device row: a switch to drive or leave the device, how the Chroma Link colors are
/// laid out on it, and its measured update rate.
/// </summary>
public sealed class DeviceViewModel(string name, Action changed) : ObservableObject
{
    private bool _isDriven = true;
    private ZoneMapping _mapping = ZoneMapping.Repeat;
    private int _lights;
    private string _rate = "";

    public string Name { get; } = name;

    public bool IsDriven
    {
        get => _isDriven;
        set
        {
            if (Set(ref _isDriven, value))
                changed();
        }
    }

    public IReadOnlyList<MappingOption> MappingOptions => MappingOption.All;

    public ZoneMapping Mapping
    {
        get => _mapping;
        set
        {
            if (Set(ref _mapping, value))
                changed();
        }
    }

    public int Lights
    {
        get => _lights;
        private set
        {
            if (Set(ref _lights, value))
                Raise(nameof(LightsText));
        }
    }

    public string LightsText => Lights == 1 ? "1 light" : $"{Lights} lights";

    public string Rate
    {
        get => _rate;
        private set => Set(ref _rate, value);
    }

    /// <summary>Sets the row from the settings without reporting it as a user change.</summary>
    public void Load(bool driven, ZoneMapping mapping)
    {
        _isDriven = driven;
        _mapping = mapping;
        Raise(nameof(IsDriven));
        Raise(nameof(Mapping));
    }

    public void Update(DeviceStatus status)
    {
        Lights = status.Lights;
        Rate = status.Excluded
            ? "Left alone"
            : $"{status.UpdatesPerSecond:F0} updates/s · {status.ApplyMilliseconds:F1} ms";
    }
}

/// <summary>
/// One provider card: its state, its devices and its color settings.
/// </summary>
public sealed class ProviderViewModel(string id, string displayName, Action changed) : ObservableObject
{
    private bool _loading;
    private bool _enabled = true;
    private double _gamma = 1;
    private double _saturation = 1;
    private double _brightness = 1;
    private string _stateText = "";
    private string _message = "";
    private string _chromaText = "";
    private string _stateColor = "#808080";

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public bool Enabled
    {
        get => _enabled;
        set => Edit(Set(ref _enabled, value));
    }

    public double Gamma
    {
        get => _gamma;
        set => Edit(Set(ref _gamma, Math.Round(value, 2)));
    }

    public double Saturation
    {
        get => _saturation;
        set => Edit(Set(ref _saturation, Math.Round(value, 2)));
    }

    public double Brightness
    {
        get => _brightness;
        set => Edit(Set(ref _brightness, Math.Round(value, 2)));
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string StateColor
    {
        get => _stateColor;
        private set => Set(ref _stateColor, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string ChromaText
    {
        get => _chromaText;
        private set => Set(ref _chromaText, value);
    }

    public bool HasDevices => Devices.Count > 0;

    public void Load(ProviderSettings settings)
    {
        _loading = true;

        Enabled = settings.Enabled;
        Gamma = settings.Gamma;
        Saturation = settings.Saturation;
        Brightness = settings.Brightness;

        _excluded = [.. settings.ExcludedDevices];
        _mappings = new Dictionary<string, ZoneMapping>(settings.Mappings, StringComparer.OrdinalIgnoreCase);

        foreach (var device in Devices)
            device.Load(!settings.IsExcluded(device.Name), settings.MappingFor(device.Name));

        _loading = false;
    }

    private List<string> _excluded = [];
    private Dictionary<string, ZoneMapping> _mappings = new(StringComparer.OrdinalIgnoreCase);

    public ProviderSettings ToSettings()
    {
        // Keep what is stored for devices that are not listed right now (unplugged, or the
        // provider is not running), and apply the rows for the ones that are.
        var excluded = new HashSet<string>(_excluded, StringComparer.OrdinalIgnoreCase);

        foreach (var device in Devices)
        {
            if (device.IsDriven)
                excluded.Remove(device.Name);
            else
                excluded.Add(device.Name);

            // Repeat is the default, so it is not stored
            if (device.Mapping == ZoneMapping.Repeat)
                _mappings.Remove(device.Name);
            else
                _mappings[device.Name] = device.Mapping;
        }

        _excluded = [.. excluded];

        return new ProviderSettings
        {
            Enabled = Enabled,
            Gamma = Gamma,
            Saturation = Saturation,
            Brightness = Brightness,
            ExcludedDevices = [.. excluded.Order(StringComparer.OrdinalIgnoreCase)],
            Mappings = new Dictionary<string, ZoneMapping>(_mappings)
        };
    }

    public void Update(ProviderStatus status)
    {
        (StateText, StateColor) = status.State switch
        {
            ProviderState.Running => ("Running", "#2E9E5B"),
            ProviderState.WaitingForDevices => ("Waiting for devices", "#D89614"),
            ProviderState.Starting => ("Starting", "#D89614"),
            ProviderState.Restarting => ("Reconnecting", "#D89614"),
            ProviderState.Faulted => ("Not working", "#D13438"),
            _ => ("Off", "#808080")
        };

        Message = status.Message;
        ChromaText = status.State == ProviderState.Running
            ? (status.ChromaConnected ? $"Razer Chroma: {status.FramesPerSecond:F0} frames/s" : "Razer Chroma: not connected")
            : "";

        // Update rows in place so checkboxes keep their state between refreshes
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!status.Devices.Any(x => string.Equals(x.Name, Devices[i].Name, StringComparison.OrdinalIgnoreCase)))
                Devices.RemoveAt(i);
        }

        foreach (var device in status.Devices)
        {
            var row = Devices.FirstOrDefault(x => string.Equals(x.Name, device.Name, StringComparison.OrdinalIgnoreCase));

            if (row == null)
            {
                row = new DeviceViewModel(device.Name, OnDeviceChanged);
                row.Load(
                    !_excluded.Contains(device.Name, StringComparer.OrdinalIgnoreCase),
                    _mappings.TryGetValue(device.Name, out var mapping) ? mapping : ZoneMapping.Repeat);
                Devices.Add(row);
            }

            row.Update(device);
        }

        Raise(nameof(HasDevices));
    }

    public void Clear()
    {
        StateText = "";
        Message = "";
        ChromaText = "";
        Devices.Clear();
        Raise(nameof(HasDevices));
    }

    private void OnDeviceChanged()
    {
        if (!_loading)
            changed();
    }

    private void Edit(bool didChange)
    {
        if (didChange && !_loading)
            changed();
    }
}

/// <summary>
/// The main window: polls the service once a second and sends settings shortly after the
/// user stops changing them.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly BridgeClient _client = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool _busy;
    private bool _settingsLoaded;
    private bool _connected;
    private bool _startsAtLogin;
    private string _helperText = "Connecting to the helper...";

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    public ICommand StartHelperCommand { get; }

    public bool Connected
    {
        get => _connected;
        private set
        {
            if (Set(ref _connected, value))
                Raise(nameof(NotConnected));
        }
    }

    public bool NotConnected => !Connected;

    /// <summary>
    /// Whether ChromaBridge starts when you log in. Turning it on or off raises a UAC prompt,
    /// because the helper's logon task can only be changed by an administrator.
    /// </summary>
    public bool StartsAtLogin
    {
        get => _startsAtLogin;
        set
        {
            if (_startsAtLogin == value)
                return;

            // Shows the requested state until the next status poll reports the real one
            if (StartupRegistration.Apply(value))
                _startsAtLogin = value;

            Raise();
        }
    }

    public string HelperText
    {
        get => _helperText;
        private set => Set(ref _helperText, value);
    }

    public MainViewModel()
    {
        foreach (var (id, displayName) in KnownProviders.All)
            Providers.Add(new ProviderViewModel(id, displayName, OnSettingsEdited));

        StartHelperCommand = new RelayCommand(StartHelper);

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _saveTimer.Tick += async (_, _) => await SaveAsync();
    }

    public void Start()
    {
        _pollTimer.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (_busy)
            return;

        _busy = true;

        try
        {
            if (!_settingsLoaded)
            {
                var settings = (await _client.GetSettingsAsync())?.Settings;

                if (settings != null)
                {
                    foreach (var provider in Providers)
                        provider.Load(settings.For(provider.Id));

                    _settingsLoaded = true;
                }
            }

            var status = (await _client.GetStatusAsync())?.Status;

            if (status == null)
            {
                Connected = false;
                _settingsLoaded = false;
                HelperText = "The ChromaBridge helper is not running.";

                foreach (var provider in Providers)
                    provider.Clear();

                return;
            }

            Connected = true;

            // The helper's task is the part that matters; the tray app's own entry follows it
            if (_startsAtLogin != status.StartsAtLogin)
            {
                _startsAtLogin = status.StartsAtLogin;
                Raise(nameof(StartsAtLogin));
            }

            HelperText = status.Elevated
                ? $"Helper v{status.Version} running as administrator"
                : $"Helper v{status.Version} running without administrator rights (ASUS needs them)";

            foreach (var provider in Providers)
            {
                var providerStatus = status.Providers.FirstOrDefault(x => string.Equals(x.Id, provider.Id, StringComparison.OrdinalIgnoreCase));

                if (providerStatus != null)
                    provider.Update(providerStatus);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSettingsEdited()
    {
        // Restart the countdown: send once the user pauses
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        _saveTimer.Stop();

        if (!_settingsLoaded)
            return;

        // Wait for a poll in flight: the client handles one request at a time
        if (_busy)
        {
            _saveTimer.Start();
            return;
        }

        _busy = true;

        try
        {
            var settings = new BridgeSettings();

            foreach (var provider in Providers)
                settings.Providers[provider.Id] = provider.ToSettings();

            await _client.SetSettingsAsync(settings);
        }
        finally
        {
            _busy = false;
        }
    }

    private void StartHelper()
    {
        try
        {
            // "runas" raises the UAC prompt: ASUS only reports devices to an administrator
            Process.Start(new ProcessStartInfo(BridgePaths.ServiceExePath) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            // The user declined the prompt
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _saveTimer.Stop();
        _client.Dispose();
    }
}
