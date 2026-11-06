// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromaBridge;

/// <summary>
/// Message types. Every message is one line of JSON.
/// </summary>
public static class MessageTypes
{
    // Tray app -> service
    public const string GetStatus = "getStatus";
    public const string GetSettings = "getSettings";
    public const string SetSettings = "setSettings";
    public const string CheckForUpdates = "checkForUpdates";
    public const string InstallUpdate = "installUpdate";

    // Service -> tray app
    public const string Status = "status";
    public const string Settings = "settings";
    public const string Error = "error";

    // Service -> provider host (the host's standard input)
    public const string ProviderSettings = "providerSettings";
    public const string Stop = "stop";

    // Provider host -> service (the host's standard output)
    public const string ProviderStatus = "providerStatus";
}

/// <summary>
/// The one envelope used on both links. Only the fields a message type needs are set.
/// </summary>
public sealed class BridgeMessage
{
    public string Type { get; set; } = "";

    public BridgeStatus? Status { get; set; }

    public BridgeSettings? Settings { get; set; }

    public ProviderStatus? ProviderStatus { get; set; }

    public ProviderSettings? ProviderSettings { get; set; }

    public string? Error { get; set; }
}

public static class BridgeJson
{
    private static readonly JsonSerializerOptions s_compact = Create(false);
    private static readonly JsonSerializerOptions s_indented = Create(true);

    public static string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? s_indented : s_compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, s_compact);

    /// <summary>Parses one protocol line, returning null when it is not a valid message.</summary>
    public static BridgeMessage? TryParseMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            var message = Deserialize<BridgeMessage>(line);

            return string.IsNullOrEmpty(message?.Type) ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public static class ProcessInfo
{
    /// <summary>True when the current process runs with administrator rights.</summary>
    public static bool IsElevated { get; } = CheckElevated();

    public static string Version { get; } = typeof(ProcessInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
