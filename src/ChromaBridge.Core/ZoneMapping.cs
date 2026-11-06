// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// How the five Chroma Link colors are laid out along a device's lights.
/// </summary>
public enum ZoneMapping
{
    /// <summary>The five colors repeat light by light: 1 2 3 4 5 1 2 3...</summary>
    Repeat,

    /// <summary>The device is split into five equal blocks, one color each.</summary>
    Stretch,

    /// <summary>The five colors are spread along the device and blended into each other.</summary>
    Gradient,

    /// <summary>Every light shows Chroma Link color 1.</summary>
    Zone1,

    Zone2,

    Zone3,

    Zone4,

    Zone5
}

public static class ZoneMapper
{
    /// <summary>The mappings in the order the UI lists them, with their display names.</summary>
    public static readonly IReadOnlyList<(ZoneMapping Mapping, string DisplayName)> All =
    [
        (ZoneMapping.Repeat, "All five, repeating"),
        (ZoneMapping.Stretch, "All five, in blocks"),
        (ZoneMapping.Gradient, "All five, blended"),
        (ZoneMapping.Zone1, "Color 1 only"),
        (ZoneMapping.Zone2, "Color 2 only"),
        (ZoneMapping.Zone3, "Color 3 only"),
        (ZoneMapping.Zone4, "Color 4 only"),
        (ZoneMapping.Zone5, "Color 5 only")
    ];

    /// <summary>
    /// Fills one color per light from the five Chroma Link colors.
    /// </summary>
    public static void Fill(Span<Rgb> lights, ReadOnlySpan<Rgb> colors, ZoneMapping mapping)
    {
        const int count = ChromaFrame.ColorCount;

        switch (mapping)
        {
            case ZoneMapping.Zone1:
            case ZoneMapping.Zone2:
            case ZoneMapping.Zone3:
            case ZoneMapping.Zone4:
            case ZoneMapping.Zone5:
                lights.Fill(colors[mapping - ZoneMapping.Zone1]);
                break;

            case ZoneMapping.Stretch:
                for (var i = 0; i < lights.Length; i++)
                    lights[i] = colors[Math.Min((int)((long)i * count / lights.Length), count - 1)];

                break;

            case ZoneMapping.Gradient:
                for (var i = 0; i < lights.Length; i++)
                {
                    // Position along the five colors: 0 at the first light, 4 at the last
                    var position = lights.Length > 1 ? i * (count - 1.0) / (lights.Length - 1) : 0;
                    var lower = Math.Min((int)position, count - 1);
                    var upper = Math.Min(lower + 1, count - 1);

                    lights[i] = Blend(colors[lower], colors[upper], position - lower);
                }

                break;

            default:
                for (var i = 0; i < lights.Length; i++)
                    lights[i] = colors[i % count];

                break;
        }
    }

    /// <summary>The array form of <see cref="Fill"/>, for callers that cannot use spans.</summary>
    public static Rgb[] Map(Rgb[] colors, int lightCount, ZoneMapping mapping)
    {
        var lights = new Rgb[lightCount];

        Fill(lights, colors, mapping);

        return lights;
    }

    private static Rgb Blend(Rgb from, Rgb to, double amount) => new(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
}
