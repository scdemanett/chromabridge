// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// The providers ChromaBridge knows about.
/// </summary>
public static class KnownProviders
{
    public const string Asus = "asus";
    public const string Corsair = "corsair";

    /// <summary>Provider IDs in display order, with their display names.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName)> All =
    [
        (Asus, "ASUS Aura"),
        (Corsair, "Corsair iCUE")
    ];

    public static string DisplayName(string id)
    {
        foreach (var provider in All)
        {
            if (string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase))
                return provider.DisplayName;
        }

        return id;
    }
}

/// <summary>
/// Settings for one provider.
/// </summary>
public sealed class ProviderSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>1 leaves colors unchanged. LEDs are close to linear, so values around 2 stop mixed colors looking washed out.</summary>
    public double Gamma { get; set; } = 1.0;

    public double Saturation { get; set; } = 1.0;

    public double Brightness { get; set; } = 1.0;

    /// <summary>Names of devices to leave alone.</summary>
    public List<string> ExcludedDevices { get; set; } = [];

    /// <summary>How the five Chroma Link colors are laid out on each device, by device name.</summary>
    public Dictionary<string, ZoneMapping> Mappings { get; set; } = [];

    /// <summary>The mapping for a device; <see cref="ZoneMapping.Repeat"/> when none is set.</summary>
    public ZoneMapping MappingFor(string deviceName)
    {
        foreach (var (name, mapping) in Mappings)
        {
            if (string.Equals(name?.Trim(), deviceName, StringComparison.OrdinalIgnoreCase))
                return mapping;
        }

        return ZoneMapping.Repeat;
    }

    public bool IsExcluded(string deviceName)
    {
        foreach (var excluded in ExcludedDevices)
        {
            if (string.Equals(excluded?.Trim(), deviceName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public ProviderSettings Clone() => new()
    {
        Enabled = Enabled,
        Gamma = Gamma,
        Saturation = Saturation,
        Brightness = Brightness,
        ExcludedDevices = [.. ExcludedDevices],
        Mappings = new Dictionary<string, ZoneMapping>(Mappings)
    };

    /// <summary>True when the set of excluded devices differs, ignoring order and case.</summary>
    public bool ExclusionsDiffer(ProviderSettings other)
    {
        var mine = new HashSet<string>(ExcludedDevices.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

        return !mine.SetEquals(other.ExcludedDevices.Select(x => x.Trim()));
    }
}

/// <summary>
/// All ChromaBridge settings.
/// </summary>
public sealed class BridgeSettings
{
    /// <summary>Settings per provider ID (lower case).</summary>
    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

    /// <summary>The settings for a provider, created with that provider's defaults when missing.</summary>
    public ProviderSettings For(string providerId)
    {
        var key = providerId.ToLowerInvariant();

        if (!Providers.TryGetValue(key, out var settings))
        {
            settings = CreateDefault(key);
            Providers[key] = settings;
        }

        return settings;
    }

    /// <summary>Fills in missing providers and normalizes keys after loading.</summary>
    public BridgeSettings Normalize()
    {
        Providers = Providers
            .Where(x => x.Value != null)
            .GroupBy(x => x.Key.ToLowerInvariant())
            .ToDictionary(x => x.Key, x => x.First().Value);

        foreach (var (id, _) in KnownProviders.All)
            For(id);

        foreach (var settings in Providers.Values)
        {
            settings.ExcludedDevices ??= [];
            settings.Mappings ??= [];
        }

        return this;
    }

    public BridgeSettings Clone() => new() { Providers = Providers.ToDictionary(x => x.Key, x => x.Value.Clone()) };

    private static ProviderSettings CreateDefault(string providerId) => providerId switch
    {
        // ASUS addressable LEDs look washed out without gamma correction
        KnownProviders.Asus => new ProviderSettings { Gamma = 2.0 },
        _ => new ProviderSettings()
    };
}

/// <summary>
/// Loads and saves <see cref="BridgeSettings"/>.
/// </summary>
public static class SettingsStore
{
    public static BridgeSettings Load()
    {
        try
        {
            if (File.Exists(BridgePaths.SettingsFile))
            {
                var settings = BridgeJson.Deserialize<BridgeSettings>(File.ReadAllText(BridgePaths.SettingsFile));

                if (settings != null)
                    return settings.Normalize();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Error("Could not read the settings file; using defaults", ex);
        }

        return new BridgeSettings().Normalize();
    }

    public static void Save(BridgeSettings settings)
    {
        Directory.CreateDirectory(BridgePaths.DataDirectory);

        // Write beside the file and swap, so a crash never leaves half a settings file
        var temp = BridgePaths.SettingsFile + ".tmp";

        File.WriteAllText(temp, BridgeJson.Serialize(settings, indented: true));
        File.Move(temp, BridgePaths.SettingsFile, overwrite: true);
    }
}
