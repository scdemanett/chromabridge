// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;

namespace ChromaBridge.Chroma;

/// <summary>
/// Receives Chroma Link frames from Razer Synapse through RzChromaBroadcastAPI64.dll, which
/// Synapse installs in System32. Razer allows one app ID per process.
/// </summary>
public sealed class ChromaBroadcastClient : IDisposable
{
    private const string Library = "RzChromaBroadcastAPI64.dll";

    private const int EventEffect = 1;
    private const int EventStatus = 2;
    private const int StatusLive = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventCallback(int type, IntPtr data);

    [DllImport(Library, EntryPoint = "Init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInit(ref Guid appId);

    [DllImport(Library, EntryPoint = "UnInit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnInit();

    [DllImport(Library, EntryPoint = "RegisterEventNotification", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeRegister(EventCallback callback);

    [DllImport(Library, EntryPoint = "UnRegisterEventNotification", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnRegister();

    // Held for as long as native code may call it
    private readonly EventCallback _callback;

    private bool _initialized;
    private bool _registered;

    /// <summary>Raised on a Razer thread for every frame. Handlers must return quickly and never throw.</summary>
    public event Action<ChromaFrame>? FrameReceived;

    /// <summary>Raised when Razer reports the broadcast as live (true) or not live (false).</summary>
    public event Action<bool>? LiveChanged;

    public ChromaBroadcastClient()
    {
        _callback = OnEvent;
    }

    /// <summary>
    /// Connects with a Razer app ID. Throws <see cref="ChromaBroadcastException"/> when Razer
    /// rejects the ID or Synapse is not installed.
    /// </summary>
    public void Start(Guid appId)
    {
        int result;

        try
        {
            result = NativeInit(ref appId);
        }
        catch (DllNotFoundException ex)
        {
            throw new ChromaBroadcastException("Razer Synapse's Chroma Broadcast library was not found. Is Synapse installed?", ex);
        }
        catch (SEHException ex)
        {
            // Razer's library throws a native exception for an app ID it does not know
            throw new ChromaBroadcastException("Razer rejected the app ID.", ex);
        }

        if (result != 0)
            throw new ChromaBroadcastException($"Razer Chroma Broadcast Init failed with code {result}.");

        _initialized = true;

        result = NativeRegister(_callback);

        if (result != 0)
            throw new ChromaBroadcastException($"Razer Chroma Broadcast RegisterEventNotification failed with code {result}.");

        _registered = true;
    }

    public void Stop()
    {
        try
        {
            if (_registered)
                NativeUnRegister();

            if (_initialized)
                NativeUnInit();
        }
        catch (Exception ex) when (ex is SEHException or DllNotFoundException)
        {
            Log.Error("Failed shutting down Razer Chroma Broadcast", ex);
        }
        finally
        {
            _registered = false;
            _initialized = false;
        }
    }

    private int OnEvent(int type, IntPtr data)
    {
        // Called from native code: an exception must never escape
        try
        {
            if (type == EventEffect && data != IntPtr.Zero)
            {
                // Five COLORREF values (0x00BBGGRR), one per Chroma Link zone
                FrameReceived?.Invoke(new ChromaFrame(
                    Rgb.FromColorRef((uint)Marshal.ReadInt32(data, 0)),
                    Rgb.FromColorRef((uint)Marshal.ReadInt32(data, 4)),
                    Rgb.FromColorRef((uint)Marshal.ReadInt32(data, 8)),
                    Rgb.FromColorRef((uint)Marshal.ReadInt32(data, 12)),
                    Rgb.FromColorRef((uint)Marshal.ReadInt32(data, 16))));
            }
            else if (type == EventStatus)
            {
                // For status events the value itself is passed in place of a pointer
                LiveChanged?.Invoke(data.ToInt64() == StatusLive);
            }
        }
        catch (Exception ex)
        {
            Log.Error("A Chroma Broadcast event handler failed", ex);
        }

        return 0;
    }

    public void Dispose() => Stop();
}

public sealed class ChromaBroadcastException : Exception
{
    public ChromaBroadcastException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
