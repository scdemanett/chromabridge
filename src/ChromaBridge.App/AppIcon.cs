// Licensed under the MIT license. See LICENSE.txt in the project root.

using Drawing = System.Drawing;

namespace ChromaBridge.App;

/// <summary>
/// The app icon, read from ChromaBridge.ico embedded in this assembly: the spectrum ring
/// with one line fanning out to two arrows, Chroma out to the other providers.
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "ChromaBridge.ico";

    /// <summary>The icon frame closest to <paramref name="size"/> pixels square.</summary>
    public static Drawing.Icon Create(int size)
    {
        using var stream = Open();

        return new Drawing.Icon(stream, size, size);
    }

    /// <summary>The whole .ico with every frame, for callers that pick their own sizes.</summary>
    public static Stream Open() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the build");
}
