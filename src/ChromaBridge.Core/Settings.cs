// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// The providers ChromaBridge knows about.
/// </summary>
public static class KnownProviders
{
    public const string Asus = "asus";
    public const string Corsair = "corsair";
    public const string OpenRgb = "openrgb";

    /// <summary>Provider IDs in display order, with their display names.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName)> All =
    [
        (Asus, "ASUS Aura"),
        (Corsair, "Corsair iCUE"),
        (OpenRgb, "OpenRGB")
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

    /// <summary>
    /// Vendor names whose devices this provider must leave alone because another provider
    /// drives them. Filled in by the service, not by the user.
    /// </summary>
    public List<string> SkipVendors { get; set; } = [];

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
        SkipVendors = [.. SkipVendors],
        Mappings = new Dictionary<string, ZoneMapping>(Mappings)
    };

    /// <summary>True when which devices are driven differs: excluded devices or skipped vendors.</summary>
    public bool ExclusionsDiffer(ProviderSettings other)
    {
        var mine = new HashSet<string>(ExcludedDevices.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

        if (!mine.SetEquals(other.ExcludedDevices.Select(x => x.Trim())))
            return true;

        return !new HashSet<string>(SkipVendors, StringComparer.OrdinalIgnoreCase).SetEquals(other.SkipVendors);
    }
}

/// <summary>
/// All ChromaBridge settings.
/// </summary>
public sealed class BridgeSettings
{
    /// <summary>Look for a new ChromaBridge version once a day. Never installs by itself.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Write detailed troubleshooting lines to the log files. Off by default.</summary>
    public bool VerboseLogging { get; set; }

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
        // Only known providers, keys lower case, and only well-formed entries: this is read
        // by elevated processes from a message the tray app sent or a file anyone could edit
        Providers = Providers
            .Where(x => x.Key != null && x.Value != null)
            .GroupBy(x => x.Key.ToLowerInvariant())
            .Where(x => KnownProviders.All.Any(k => k.Id == x.Key))
            .ToDictionary(x => x.Key, x => x.First().Value);

        foreach (var (id, _) in KnownProviders.All)
            For(id);

        foreach (var settings in Providers.Values)
        {
            settings.ExcludedDevices = [.. (settings.ExcludedDevices ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
            settings.Mappings = (settings.Mappings ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && Enum.IsDefined(x.Value))
                .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);
            settings.SkipVendors = [];
            settings.Gamma = double.IsFinite(settings.Gamma) ? Math.Clamp(settings.Gamma, 0.5, 4) : 1;
            settings.Saturation = double.IsFinite(settings.Saturation) ? Math.Clamp(settings.Saturation, 0, 2) : 1;
            settings.Brightness = double.IsFinite(settings.Brightness) ? Math.Clamp(settings.Brightness, 0, 1) : 1;
        }

        // OpenRGB can see the same hardware as the native providers; two writers on one
        // device fight, so OpenRGB leaves a vendor alone while its native provider is on.
        var openRgb = For(KnownProviders.OpenRgb);

        // Razer's own devices are lit by Synapse, which is also where the colors come from
        openRgb.SkipVendors.Add("Razer");

        if (For(KnownProviders.Asus).Enabled)
            openRgb.SkipVendors.Add("ASUS");

        if (For(KnownProviders.Corsair).Enabled)
            openRgb.SkipVendors.Add("Corsair");

        return this;
    }

    public BridgeSettings Clone() => new()
    {
        CheckForUpdates = CheckForUpdates,
        VerboseLogging = VerboseLogging,
        Providers = Providers.ToDictionary(x => x.Key, x => x.Value.Clone())
    };

    private static ProviderSettings CreateDefault(string providerId) => providerId switch
    {
        // ASUS addressable LEDs look washed out without gamma correction
        KnownProviders.Asus => new ProviderSettings { Gamma = 2.0 },

        // Off until the user turns it on: it needs OpenRGB installed and running
        KnownProviders.OpenRgb => new ProviderSettings { Enabled = false },
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
