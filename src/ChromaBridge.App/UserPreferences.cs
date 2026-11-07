// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO;
using System.Text.Json;

namespace ChromaBridge.App;

/// <summary>
/// Choices that belong to this user's copy of the tray app, not to the helper: kept beside the
/// window placement in the user's local app data.
/// </summary>
internal sealed class UserPreferences
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromaBridge", "preferences.json");

    /// <summary>
    /// Whether the tray icon is shown. With it hidden the app runs invisibly; starting
    /// ChromaBridge again (from the Start menu, say) brings the window up.
    /// </summary>
    public bool ShowTrayIcon { get; set; } = true;

    public static UserPreferences Load()
    {
        try
        {
            if (File.Exists(s_path))
                return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(s_path)) ?? new UserPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to the defaults
        }

        return new UserPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
            File.WriteAllText(s_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still applies for this run
        }
    }
}
