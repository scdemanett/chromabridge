// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace ChromaBridge.App;

/// <summary>
/// Draws the app icon in code: a ring through the color wheel. A placeholder until the
/// project has artwork of its own.
/// </summary>
internal static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Drawing.Icon Create(int size = 32)
    {
        using var bitmap = new Drawing.Bitmap(size, size);

        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);

            var thickness = size / 5f;
            var bounds = new Drawing.RectangleF(thickness / 2 + 1, thickness / 2 + 1, size - thickness - 2, size - thickness - 2);

            const int segments = 36;

            for (var i = 0; i < segments; i++)
            {
                using var pen = new Drawing.Pen(FromHue(i * 360.0 / segments), thickness);

                graphics.DrawArc(pen, bounds, i * 360f / segments - 90, 360f / segments + 1.5f);
            }
        }

        var handle = bitmap.GetHicon();

        try
        {
            // Clone so the icon owns its data and the GDI handle can be freed
            using var icon = Drawing.Icon.FromHandle(handle);

            return (Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Drawing.Color FromHue(double hue)
    {
        var sector = hue / 60.0;
        var x = (int)Math.Round(255 * (1 - Math.Abs(sector % 2 - 1)));

        return (int)sector switch
        {
            0 => Drawing.Color.FromArgb(255, x, 0),
            1 => Drawing.Color.FromArgb(x, 255, 0),
            2 => Drawing.Color.FromArgb(0, 255, x),
            3 => Drawing.Color.FromArgb(0, x, 255),
            4 => Drawing.Color.FromArgb(x, 0, 255),
            _ => Drawing.Color.FromArgb(255, 0, x)
        };
    }
}
