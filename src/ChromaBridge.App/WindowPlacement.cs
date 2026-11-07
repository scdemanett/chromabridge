// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO;
using System.Text.Json;
using System.Windows;

namespace ChromaBridge.App;

/// <summary>
/// Where the window was last time, so it comes back there. Stored per user, since the tray app
/// runs as the user; the helper's settings folder is admin-only.
/// </summary>
internal sealed class WindowPlacement
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromaBridge", "window.json");

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>Puts the window where it last was, if that place is still on a screen.</summary>
    public static void Restore(Window window)
    {
        var saved = Load();

        if (saved == null || saved.Width < window.MinWidth || saved.Height < window.MinHeight)
            return;

        // The virtual screen is every monitor together; a window off all of them (a monitor
        // that was unplugged, say) would be unreachable, so it falls back to the default spot
        var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        bounds.Intersect(screens);

        if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100)
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = saved.Left;
        window.Top = saved.Top;
        window.Width = saved.Width;
        window.Height = saved.Height;

        if (saved.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>Records where the window is now. A minimized window keeps its last normal place.</summary>
    public static void Save(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.IsEmpty)
            return;

        var placement = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = window.WindowState == WindowState.Maximized
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
            File.WriteAllText(s_path, JsonSerializer.Serialize(placement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth bothering the user over; the window just opens at the default spot next time
        }
    }

    private static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(s_path))
                return null;

            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(s_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
