// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Reflection;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ChromaBridge.App;

/// <summary>
/// Shows a tray notification with the app's own icon in it. Windows Forms' NotifyIcon only
/// offers the stock information, warning and error icons, but the shell accepts any icon
/// (NIIF_USER), so this sends the same request itself, through the NotifyIcon's own window
/// and id. Clicks still reach NotifyIcon's BalloonTipClicked event. If the NotifyIcon
/// internals are not where this expects them, the stock icon is used instead.
/// </summary>
internal static class TrayBalloon
{
    private const uint NimModify = 0x1;
    private const uint NifInfo = 0x10;
    private const uint NiifUser = 0x4;
    private const uint NiifLargeIcon = 0x20;

    /// <summary>The shell keeps using the icon after the call, so it is held for the app's lifetime.</summary>
    private static Drawing.Icon? s_balloonIcon;

    public static void Show(Forms.NotifyIcon trayIcon, string title, string text, int timeoutMilliseconds)
    {
        if (!TryShowWithAppIcon(trayIcon, title, text, timeoutMilliseconds))
            trayIcon.ShowBalloonTip(timeoutMilliseconds, title, text, Forms.ToolTipIcon.Info);
    }

    private static bool TryShowWithAppIcon(Forms.NotifyIcon trayIcon, string title, string text, int timeoutMilliseconds)
    {
        try
        {
            var type = typeof(Forms.NotifyIcon);
            var idField = type.GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance);
            var windowField = type.GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);

            if (idField?.GetValue(trayIcon) is not uint id || windowField?.GetValue(trayIcon) is not Forms.NativeWindow window)
                return false;

            // The icon must exist in the tray before it can carry a balloon
            if (window.Handle == IntPtr.Zero || !trayIcon.Visible)
                return false;

            // The large icon is shown at 32 px (NIIF_LARGE_ICON); a 48 px frame scales down cleanly on high DPI
            s_balloonIcon ??= AppIcon.Create(48);

            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = window.Handle,
                uID = id,
                uFlags = NifInfo,
                szInfo = Truncate(text, 255),
                uTimeoutOrVersion = (uint)Math.Clamp(timeoutMilliseconds, 10000, 30000),
                szInfoTitle = Truncate(title, 63),
                dwInfoFlags = NiifUser | NiifLargeIcon,
                hBalloonIcon = s_balloonIcon.Handle
            };

            return Shell_NotifyIconW(NimModify, ref data);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or MemberAccessException or TargetInvocationException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    /// <summary>NOTIFYICONDATAW, the Windows Vista and later layout.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
