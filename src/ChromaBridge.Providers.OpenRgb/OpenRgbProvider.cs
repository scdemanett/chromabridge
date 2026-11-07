// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Net.Sockets;

namespace ChromaBridge.Providers.OpenRgb;

/// <summary>
/// Drives every device OpenRGB supports through OpenRGB's SDK server on this PC. The
/// service downloads and runs OpenRGB for the user (see OpenRgbManager); an OpenRGB the user
/// runs themselves with its SDK server on works too.
///
/// OpenRGB talks to hardware directly, so it cannot share a device with the vendor's own
/// software. Controllers from vendors that ChromaBridge already drives natively are skipped
/// here (see <see cref="ProviderSettings.SkipVendors"/>); the user should also turn those
/// devices off inside OpenRGB.
/// </summary>
public sealed class OpenRgbProvider : ILightingProvider
{
    private const string Host = "127.0.0.1";

    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(5);

    private OpenRgbClient? _client;
    private IReadOnlyList<string> _skipVendors = [];

    public void Configure(ProviderSettings settings) => _skipVendors = [.. settings.SkipVendors];

    public IReadOnlyList<ILightingDevice> Connect(Action<string> report, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _client ??= OpenRgbClient.Connect(Host, OpenRgbClient.DefaultPort, BridgePaths.ProductName);

                if (attempt == 1 || attempt % 12 == 0)
                    Log.Info($"Connected to OpenRGB (SDK protocol {_client.ProtocolVersion})");

                var devices = FindDevices(_client);

                if (devices.Count > 0)
                {
                    _client.StartListening();
                    return devices;
                }

                report("OpenRGB is running but has no devices to drive yet.");
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                if (attempt == 1 || attempt % 12 == 0)
                    Log.Info($"Attempt {attempt}: OpenRGB is not reachable ({ex.Message})");

                _client?.Dispose();
                _client = null;

                report("Waiting for OpenRGB to start...");
            }

            if (cancellationToken.WaitHandle.WaitOne(s_retryDelay))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Disconnect()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Disconnect();

    private List<ILightingDevice> FindDevices(OpenRgbClient client)
    {
        var devices = new List<ILightingDevice>();
        var controllers = client.GetControllers();

        foreach (var group in controllers.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var number = 1;

            foreach (var controller in group)
            {
                // Names identify devices in the settings, so identical ones get a number
                var name = group.Count() == 1 ? controller.Name : $"{controller.Name} {number++}";

                if (controller.LedCount == 0)
                {
                    Log.Info($"Skipped \"{name}\": it has no LEDs");
                    continue;
                }

                if (_skipVendors.Any(x => controller.Vendor.Contains(x, StringComparison.OrdinalIgnoreCase) || controller.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info($"Skipped \"{name}\" ({controller.Vendor}): ChromaBridge drives this vendor through its own provider");
                    continue;
                }

                client.SetCustomMode(controller.Index);

                Log.Info($"Device \"{name}\": vendor \"{controller.Vendor}\", {controller.LedCount} light(s)");

                devices.Add(new OpenRgbDevice(client, controller.Index, name, controller.LedCount));
            }
        }

        return devices;
    }
}

internal sealed class OpenRgbDevice(OpenRgbClient client, int index, string name, int lightCount) : ILightingDevice
{
    private readonly Rgb[] _last = new Rgb[lightCount];
    private bool _written;

    public string Name { get; } = name;

    public int LightCount { get; } = lightCount;

    public void Apply(ReadOnlySpan<Rgb> colors)
    {
        // Throwing tells the host to reconnect, which lists the controllers again
        if (client.Closed)
            throw new IOException("The connection to OpenRGB was lost.");

        if (client.DeviceListChanged)
            throw new IOException("OpenRGB's device list changed.");

        if (_written && colors.SequenceEqual(_last))
            return;

        client.UpdateLeds(index, colors);

        colors.CopyTo(_last);
        _written = true;
    }
}
