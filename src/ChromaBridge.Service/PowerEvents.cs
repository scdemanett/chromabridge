// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;

namespace ChromaBridge.Service;

/// <summary>
/// Logs when the PC goes to sleep and wakes. Lighting problems cluster around standby
/// (Synapse stops sending, vendor services restart), so the log needs those moments marked.
/// Uses the callback form of the power notification API, which needs no window.
/// </summary>
internal static class PowerEvents
{
    private const int DeviceNotifyCallback = 2;

    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    // Held for as long as Windows may call it
    private static readonly DeviceNotifyCallbackRoutine s_callback = OnPowerEvent;
    private static IntPtr s_registration;
    private static IntPtr s_parameters;
    private static long s_sleptAt;

    public static void Start()
    {
        try
        {
            s_parameters = Marshal.AllocHGlobal(Marshal.SizeOf<SubscribeParameters>());
            Marshal.StructureToPtr(new SubscribeParameters { Callback = s_callback }, s_parameters, false);

            var result = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, s_parameters, out s_registration);

            if (result != 0)
                Log.Warn($"Sleep and wake will not be logged: power notification registration returned {result}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Sleep and wake will not be logged: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Stop()
    {
        if (s_registration != IntPtr.Zero)
        {
            PowerUnregisterSuspendResumeNotification(s_registration);
            s_registration = IntPtr.Zero;
        }

        if (s_parameters != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(s_parameters);
            s_parameters = IntPtr.Zero;
        }
    }

    private static uint OnPowerEvent(IntPtr context, int type, IntPtr setting)
    {
        // Called on a system thread: never throw
        try
        {
            switch (type)
            {
                case PbtApmSuspend:
                    s_sleptAt = Environment.TickCount64;
                    Log.Info("The PC is going to sleep");
                    break;

                case PbtApmResumeAutomatic:
                    Log.Info(s_sleptAt == 0
                        ? "The PC woke from sleep"
                        : $"The PC woke from sleep after {(Environment.TickCount64 - s_sleptAt) / 60000} min");
                    break;

                case PbtApmResumeSuspend:
                    Log.Info("The user is back after sleep");
                    break;
            }
        }
        catch (Exception)
        {
        }

        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(int flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
