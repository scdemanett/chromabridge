// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// Applies saturation, gamma and brightness to colors. Immutable: build a new one when
/// the settings change.
/// </summary>
public sealed class ColorCorrection
{
    public static readonly ColorCorrection Identity = new(1, 1, 1);

    private readonly byte[] _table = new byte[256];

    public double Saturation { get; }

    public double Gamma { get; }

    public double Brightness { get; }

    public bool IsIdentity { get; }

    public ColorCorrection(double saturation, double gamma, double brightness)
    {
        Saturation = Clamp(saturation, 0, 4, 1);
        Gamma = Clamp(gamma, 0.2, 5, 1);
        Brightness = Clamp(brightness, 0, 1, 1);

        for (var i = 0; i < _table.Length; i++)
            _table[i] = (byte)Math.Round(Math.Pow(i / 255.0, Gamma) * 255.0 * Brightness);

        IsIdentity = Saturation == 1 && Gamma == 1 && Brightness == 1;
    }

    public ColorCorrection(ProviderSettings settings)
        : this(settings.Saturation, settings.Gamma, settings.Brightness)
    {
    }

    public Rgb Apply(Rgb color)
    {
        if (IsIdentity)
            return color;

        // Saturation keeps the strongest channel and moves the others away from it, so a
        // color gets deeper without getting dimmer.
        var max = Math.Max(color.R, Math.Max(color.G, color.B));

        return new Rgb(_table[Saturate(color.R, max)], _table[Saturate(color.G, max)], _table[Saturate(color.B, max)]);
    }

    private int Saturate(int channel, int max) => Math.Clamp((int)Math.Round(max + ((channel - max) * Saturation)), 0, 255);

    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
