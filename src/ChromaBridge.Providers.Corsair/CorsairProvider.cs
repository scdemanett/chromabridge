// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;

namespace ChromaBridge.Providers.Corsair;

/// <summary>
/// Drives Corsair devices through iCUE with the iCUE SDK v4.
///
/// The SDK runs in shared mode: iCUE keeps its own lighting on a layer with priority 127 and
/// SDK clients draw on top at 128, so there is no control to take or hand back. When this
/// provider disconnects, iCUE's own lighting shows again by itself.
/// </summary>
public sealed class CorsairProvider : ILightingProvider
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(5);

    private readonly ManualResetEventSlim _stateChanged = new(false);

    // Held for as long as the SDK may call it
    private readonly Native.SessionStateChangedHandler _onStateChanged;

    private volatile int _state = Native.StateClosed;
    private bool _connectCalled;

    public CorsairProvider()
    {
        _onStateChanged = OnSessionStateChanged;
        IcueSdk.RegisterResolver();
    }

    public IReadOnlyList<ILightingDevice> Connect(Action<string> report, CancellationToken cancellationToken)
    {
        if (!IcueSdk.Ensure(report, cancellationToken))
            throw new InvalidOperationException("The iCUE SDK library could not be downloaded. ChromaBridge will try again.");

        report("Connecting to iCUE...");

        try
        {
            var error = Native.CorsairConnect(_onStateChanged, IntPtr.Zero);

            _connectCalled = true;

            if (error != Native.Success)
                throw new InvalidOperationException($"CorsairConnect failed: {Native.Describe(error)}.");
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("The iCUE SDK library (iCUESDK.x64_2019.dll) could not be loaded.", ex);
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_state == Native.StateConnected)
            {
                var devices = FindDevices();

                if (devices.Count > 0)
                    return devices;

                report("iCUE is connected but has not reported any devices with lights yet.");
            }
            else
            {
                // The SDK keeps retrying by itself; this loop only reports and waits
                report(_state == Native.StateConnectionRefused
                    ? "iCUE refused the connection. Turn on the SDK in iCUE's settings."
                    : "Waiting for iCUE. Is it running?");
            }

            if (attempt == 1 || attempt % 12 == 0)
                Log.Info($"Attempt {attempt}: no Corsair devices yet (session state {_state})");

            _stateChanged.Reset();

            // Wake early when the session state changes
            WaitHandle.WaitAny([_stateChanged.WaitHandle, cancellationToken.WaitHandle], s_retryDelay);
        }
    }

    public void Disconnect()
    {
        if (!_connectCalled)
            return;

        try
        {
            Native.CorsairDisconnect();
            Log.Info("Disconnected from iCUE");
        }
        catch (Exception ex) when (ex is DllNotFoundException or SEHException)
        {
            Log.Error("Failed disconnecting from iCUE", ex);
        }

        _connectCalled = false;
    }

    public void Dispose() => Disconnect();

    private List<ILightingDevice> FindDevices()
    {
        var infos = new Native.DeviceInfo[Native.DeviceCountMax];
        var filter = new Native.DeviceFilter { DeviceTypeMask = Native.DeviceTypeAll };

        var error = Native.CorsairGetDevices(ref filter, infos.Length, infos, out var count);

        if (error != Native.Success)
        {
            Log.Warn($"CorsairGetDevices failed: {Native.Describe(error)}");
            return [];
        }

        // Sort by serial so numbering of identical models (four memory modules, say) is
        // the same on every start
        var found = infos.Take(count)
            .Where(x => x.LedCount > 0)
            .OrderBy(x => x.ModelText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SerialText, StringComparer.Ordinal)
            .ThenBy(x => x.IdText, StringComparer.Ordinal)
            .ToList();

        var devices = new List<ILightingDevice>();

        foreach (var group in found.GroupBy(x => x.ModelText, StringComparer.OrdinalIgnoreCase))
        {
            var index = 1;

            foreach (var info in group)
            {
                // Names identify devices in the settings, so identical models get a number
                var name = group.Count() == 1 ? info.ModelText : $"{info.ModelText} {index++}";
                var device = CorsairDevice.TryCreate(info, name);

                if (device == null)
                    continue;

                Log.Info($"Device \"{name}\": type 0x{info.Type:X4}, {device.LightCount} light(s)");

                devices.Add(device);
            }
        }

        return devices;
    }

    private void OnSessionStateChanged(IntPtr context, IntPtr eventData)
    {
        // Called on an SDK thread: never throw
        try
        {
            if (eventData == IntPtr.Zero)
                return;

            _state = Marshal.ReadInt32(eventData);

            Log.Info($"iCUE session state: {Native.DescribeState(_state)}");

            _stateChanged.Set();
        }
        catch (Exception ex)
        {
            Log.Error("The iCUE session handler failed", ex);
        }
    }
}

internal sealed class CorsairDevice : ILightingDevice
{
    private readonly byte[] _id;
    private readonly Native.LedColor[] _colors;
    private bool _written;

    public string Name { get; }

    public int LightCount => _colors.Length;

    private CorsairDevice(byte[] id, string name, Native.LedColor[] colors)
    {
        _id = id;
        Name = name;
        _colors = colors;
    }

    public static CorsairDevice? TryCreate(Native.DeviceInfo info, string name)
    {
        var positions = new Native.LedPosition[Native.DeviceLedCountMax];
        var error = Native.CorsairGetLedPositions(info.Id, positions.Length, positions, out var count);

        if (error != Native.Success || count <= 0)
        {
            Log.Warn($"Could not read the lights of \"{name}\": {Native.Describe(error)}");
            return null;
        }

        var colors = new Native.LedColor[count];

        for (var i = 0; i < count; i++)
            colors[i] = new Native.LedColor { Id = positions[i].Id, A = 255 };

        return new CorsairDevice(info.Id, name, colors);
    }

    public void Apply(ReadOnlySpan<Rgb> colors)
    {
        var changed = !_written;
        var count = Math.Min(colors.Length, _colors.Length);

        for (var i = 0; i < count; i++)
        {
            var color = colors[i];

            if (_colors[i].R == color.R && _colors[i].G == color.G && _colors[i].B == color.B)
                continue;

            _colors[i].R = color.R;
            _colors[i].G = color.G;
            _colors[i].B = color.B;
            changed = true;
        }

        if (!changed)
            return;

        var error = Native.CorsairSetLedColors(_id, _colors.Length, _colors);

        // Throwing tells the host the device is gone, for example after iCUE restarts
        if (error != Native.Success)
            throw new InvalidOperationException($"CorsairSetLedColors failed: {Native.Describe(error)}.");

        _written = true;
    }
}

/// <summary>
/// The parts of iCUESDK.h (SDK 4.0.84) this provider uses.
/// </summary>
internal static class Native
{
    private const string Library = "iCUESDK.x64_2019.dll";

    public const int StringSize = 128;
    public const int DeviceCountMax = 64;
    public const int DeviceLedCountMax = 512;
    public const int DeviceTypeAll = unchecked((int)0xFFFFFFFF);

    public const int Success = 0;

    public const int StateClosed = 1;
    public const int StateConnectionRefused = 4;
    public const int StateConnected = 6;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SessionStateChangedHandler(IntPtr context, IntPtr eventData);

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceFilter
    {
        public int DeviceTypeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int Type;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = StringSize)]
        public byte[] Id;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = StringSize)]
        public byte[] Serial;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = StringSize)]
        public byte[] Model;

        public int LedCount;

        public int ChannelCount;

        public readonly string IdText => Text(Id);

        public readonly string SerialText => Text(Serial);

        public readonly string ModelText => Text(Model) is { Length: > 0 } model ? model : "Corsair Device";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LedPosition
    {
        public uint Id;
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LedColor
    {
        public uint Id;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CorsairConnect(SessionStateChangedHandler onStateChanged, IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CorsairDisconnect();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CorsairGetDevices(ref DeviceFilter filter, int sizeMax, [In, Out] DeviceInfo[] devices, out int size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CorsairGetLedPositions(byte[] deviceId, int sizeMax, [In, Out] LedPosition[] ledPositions, out int size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CorsairSetLedColors(byte[] deviceId, int size, [In] LedColor[] ledColors);

    public static string Describe(int error) => error switch
    {
        0 => "success",
        1 => "not connected to iCUE",
        2 => "another app has exclusive control",
        3 => "incompatible protocol",
        4 => "invalid arguments",
        5 => "invalid operation",
        6 => "device not found",
        7 => "not allowed",
        _ => $"error {error}"
    };

    public static string DescribeState(int state) => state switch
    {
        1 => "closed",
        2 => "connecting",
        3 => "timeout",
        4 => "connection refused",
        5 => "connection lost",
        6 => "connected",
        _ => $"state {state}"
    };

    private static string Text(byte[]? value)
    {
        if (value == null)
            return "";

        var length = Array.IndexOf(value, (byte)0);

        return System.Text.Encoding.UTF8.GetString(value, 0, length < 0 ? value.Length : length).Trim();
    }
}
