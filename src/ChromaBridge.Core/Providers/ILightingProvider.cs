// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge.Providers;

/// <summary>
/// A vendor's lighting system: ASUS Aura, Corsair iCUE and so on. One provider runs per
/// host process.
/// </summary>
public interface ILightingProvider : IDisposable
{
    /// <summary>One of <see cref="KnownProviders"/>.</summary>
    string Id { get; }

    /// <summary>
    /// Waits until the vendor software reports devices, takes lighting control, and returns
    /// the devices. Blocks, retrying, until there is at least one device or the token is
    /// cancelled.
    /// </summary>
    /// <param name="report">Called with a plain-words progress message for the UI.</param>
    IReadOnlyList<ILightingDevice> Connect(Action<string> report, CancellationToken cancellationToken);

    /// <summary>Hands lighting control back to the vendor software.</summary>
    void Disconnect();
}

/// <summary>
/// One device a provider can light.
/// </summary>
public interface ILightingDevice
{
    string Name { get; }

    int LightCount { get; }

    /// <summary>
    /// Writes one color per light and sends them to the hardware. Throws when the device
    /// can no longer be reached.
    /// </summary>
    void Apply(ReadOnlySpan<Rgb> colors);
}
