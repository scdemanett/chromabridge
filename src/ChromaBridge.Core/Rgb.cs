// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// A color with 8 bits per channel.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{

    /// <summary>
    /// Reads a Windows COLORREF value (0x00BBGGRR), the format Razer and ASUS both use.
    /// </summary>
    public static Rgb FromColorRef(uint value) => new((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

    /// <summary>
    /// The color as a Windows COLORREF value (0x00BBGGRR).
    /// </summary>
    public uint ToColorRef() => (uint)(R | (G << 8) | (B << 16));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// One frame from Razer Chroma Broadcast: the five Chroma Link colors.
/// </summary>
public readonly record struct ChromaFrame(Rgb Color1, Rgb Color2, Rgb Color3, Rgb Color4, Rgb Color5)
{
    public const int ColorCount = 5;

    public Rgb this[int index] => index switch
    {
        0 => Color1,
        1 => Color2,
        2 => Color3,
        3 => Color4,
        4 => Color5,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public override string ToString() => $"{Color1} {Color2} {Color3} {Color4} {Color5}";
}
