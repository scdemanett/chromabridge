// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Security.Cryptography;
using System.Text.Json;

namespace ChromaBridge.Chroma;

/// <summary>
/// The Razer app ID each provider connects with. Razer only accepts IDs it has registered,
/// so the built-in ones are the IDs issued to the original Chroma Control project. To use
/// IDs of your own, put them in appids.json in the data folder, for example
/// <c>{ "asus": "00000000-0000-0000-0000-000000000000" }</c>.
/// </summary>
public static class AppIds
{
    private const string BuiltInFile = "AppIds.dat";

    /// <summary>The name each provider's ID has in the built-in file.</summary>
    private static readonly Dictionary<string, string> s_builtInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [KnownProviders.Asus] = "ChromaControl.Asus",
        [KnownProviders.Corsair] = "ChromaControl.Corsair"
    };

    public static Guid Get(string providerId)
    {
        var overridden = ReadOverride(providerId);

        if (overridden != null)
            return overridden.Value;

        if (!s_builtInNames.TryGetValue(providerId, out var name))
            throw new InvalidOperationException($"No Razer app ID is known for provider '{providerId}'.");

        return ReadBuiltIn()[name];
    }

    private static Guid? ReadOverride(string providerId)
    {
        try
        {
            if (!File.Exists(BridgePaths.AppIdOverrideFile))
                return null;

            var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BridgePaths.AppIdOverrideFile));

            if (ids == null)
                return null;

            foreach (var (key, value) in ids)
            {
                if (string.Equals(key, providerId, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out var id))
                    return id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Could not read the app ID override file", ex);
        }

        return null;
    }

    /// <summary>
    /// Reads the original project's ID file: a 32 byte AES key and 16 byte IV, then the
    /// encrypted list of name and GUID pairs.
    /// </summary>
    private static Dictionary<string, Guid> ReadBuiltIn()
    {
        var ids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Data", BuiltInFile));
        using var aes = Aes.Create();

        var key = new byte[32];
        var iv = new byte[16];

        file.ReadExactly(key);
        file.ReadExactly(iv);

        using var crypto = new CryptoStream(file, aes.CreateDecryptor(key, iv), CryptoStreamMode.Read);
        using var reader = new BinaryReader(crypto);

        var count = reader.ReadInt32();

        for (var i = 0; i < count; i++)
            ids[reader.ReadString()] = Guid.Parse(reader.ReadString());

        return ids;
    }
}
