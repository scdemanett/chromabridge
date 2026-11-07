// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChromaBridge.Providers.Asus;

/// <summary>
/// Drives ASUS Aura devices through the Aura SDK that Armoury Crate installs.
///
/// Things the SDK does not tell you:
/// - It only reports devices to an elevated process.
/// - Armoury Crate grants control by exe path through its in-game lighting order;
///   SwitchMode() silently does nothing when another app has higher priority.
/// - Enumerate() caches its result per SDK object, so each attempt uses a fresh object.
/// </summary>
public sealed class AsusProvider : ILightingProvider
{
    /// <summary>The AniMe Matrix is a pixel display, not lighting; Armoury Crate keeps it.</summary>
    private const uint AnimeMatrixDeviceType = 0x000F8100;

    private static readonly TimeSpan s_initialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(30);

    private IAuraSdk? _sdk;
    private bool _hasControl;

    public string Id => KnownProviders.Asus;

    public IReadOnlyList<ILightingDevice> Connect(Action<string> report, CancellationToken cancellationToken)
    {
        if (!ProcessInfo.IsElevated)
            Log.Warn("Not running as administrator: ASUS will report no devices");

        WaitForLightingService(report, cancellationToken);

        var delay = s_initialRetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                report(attempt == 1 ? "Looking for ASUS devices..." : $"Looking for ASUS devices (attempt {attempt})...");

                var devices = TryConnect(attempt);

                if (devices.Count > 0)
                    return devices;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                Log.Error($"Attempt {attempt}: the Aura SDK call failed", ex);
            }

            ReleaseSdk();

            report(ProcessInfo.IsElevated
                ? "Armoury Crate has not reported any devices yet."
                : "ASUS only reports devices to an administrator. The helper is not elevated.");

            Log.Info($"Attempt {attempt}: no ASUS devices, retrying in {delay.TotalSeconds:F0} s");

            if (cancellationToken.WaitHandle.WaitOne(delay))
                cancellationToken.ThrowIfCancellationRequested();

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, s_maxRetryDelay.Ticks));
        }
    }

    public void Disconnect()
    {
        ReleaseControl();
        ReleaseSdk();
    }

    public void Dispose() => Disconnect();

    private List<ILightingDevice> TryConnect(int attempt)
    {
        _sdk = AuraSdk.Create();

        var found = Enumerate(_sdk);

        Log.Info($"Attempt {attempt}: Aura reported {found.Count} device(s)");

        if (found.Count == 0)
            return [];

        // Take control, then enumerate again and use those objects
        _sdk.SwitchMode();
        _hasControl = true;

        Log.Info("Requested control of Aura lighting (the SDK does not report whether Armoury Crate granted it)");

        var devices = new List<ILightingDevice>();

        foreach (var device in Enumerate(_sdk))
        {
            var name = ReadName(device);

            if (IsAnimeMatrix(device, name))
            {
                Log.Info($"Skipped \"{name}\": the AniMe Matrix display is left to Armoury Crate");
                continue;
            }

            var asusDevice = new AsusDevice(device, name);

            Log.Info($"Device \"{name}\": type 0x{ReadType(device):X8}, {asusDevice.LightCount} light(s)");

            if (asusDevice.LightCount > 0)
                devices.Add(asusDevice);
        }

        if (devices.Count == 0)
            ReleaseControl();

        return devices;
    }

    private static List<IAuraSyncDevice> Enumerate(IAuraSdk sdk)
    {
        var result = new List<IAuraSyncDevice>();
        var collection = sdk.Enumerate(0);

        if (collection != null)
        {
            var count = collection.Count;

            for (var i = 0; i < count; i++)
                result.Add(collection[i]);
        }

        return result;
    }

    private void ReleaseControl()
    {
        if (!_hasControl || _sdk == null)
            return;

        try
        {
            if (_sdk is IAuraSdk2 sdk2)
                sdk2.ReleaseControl(0);
            else
                _sdk.SwitchMode();

            Log.Info("Released control of Aura lighting");
        }
        catch (COMException ex)
        {
            Log.Error("Failed releasing control of Aura lighting", ex);
        }
        finally
        {
            _hasControl = false;
        }
    }

    private void ReleaseSdk()
    {
        ReleaseControl();

        if (_sdk != null && Marshal.IsComObject(_sdk))
            Marshal.FinalReleaseComObject(_sdk);

        _sdk = null;
    }

    private static void WaitForLightingService(Action<string> report, CancellationToken cancellationToken)
    {
        var reported = false;

        while (!IsLightingServiceRunning())
        {
            if (!reported)
            {
                report("Waiting for the ASUS LightingService. Is Armoury Crate installed?");
                Log.Info("Waiting for the ASUS LightingService process");
                reported = true;
            }

            if (cancellationToken.WaitHandle.WaitOne(2000))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool IsLightingServiceRunning()
    {
        var processes = Process.GetProcessesByName("LightingService");

        foreach (var process in processes)
            process.Dispose();

        return processes.Length != 0;
    }

    private static bool IsAnimeMatrix(IAuraSyncDevice device, string name) =>
        ReadType(device) == AnimeMatrixDeviceType || name.Contains("Matrix", StringComparison.OrdinalIgnoreCase);

    private static string ReadName(IAuraSyncDevice device)
    {
        try
        {
            return device.Name ?? "Unknown Aura Device";
        }
        catch (COMException)
        {
            return "Unknown Aura Device";
        }
    }

    private static uint ReadType(IAuraSyncDevice device)
    {
        try
        {
            return device.Type;
        }
        catch (COMException)
        {
            return 0;
        }
    }
}

/// <summary>
/// One Aura device. Setting a light's color is free; all the time goes into Apply(), about
/// 27 ms for a 120 LED addressable header.
/// </summary>
internal sealed class AsusDevice : ILightingDevice
{
    private readonly IAuraSyncDevice _device;
    private readonly IAuraRgbLight[] _lights;
    private readonly uint?[] _lastColors;

    public string Name { get; }

    public int LightCount => _lights.Length;

    public AsusDevice(IAuraSyncDevice device, string name)
    {
        _device = device;
        Name = name;

        var lights = new List<IAuraRgbLight>();

        try
        {
            var collection = _device.Lights;
            var count = collection.Count;

            for (var i = 0; i < count; i++)
                lights.Add(collection[i]);
        }
        catch (COMException ex)
        {
            Log.Error($"Failed reading the lights of \"{name}\"", ex);
        }

        _lights = [.. lights];
        _lastColors = new uint?[_lights.Length];
    }

    public void Apply(ReadOnlySpan<Rgb> colors)
    {
        var changed = false;
        var count = Math.Min(colors.Length, _lights.Length);

        for (var i = 0; i < count; i++)
        {
            var color = colors[i].ToColorRef();

            if (_lastColors[i] == color)
                continue;

            _lights[i].Color = color;
            _lastColors[i] = color;
            changed = true;
        }

        if (changed)
            _device.Apply();
    }
}
