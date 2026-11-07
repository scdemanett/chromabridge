// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChromaBridge;

/// <summary>One third-party download: where it comes from and the hash it must have.</summary>
public sealed record Bundle(string Id, string Version, string FileName, string Url, string Sha256);

/// <summary>
/// The third-party pieces ChromaBridge needs at run time, pinned in bundles.json at the
/// repository root. The Full installer ships them in the install folder's "bundled" folder;
/// the Lite installer leaves them to be downloaded on first use.
/// </summary>
public static class Bundles
{
    private static readonly Lazy<IReadOnlyDictionary<string, Bundle>> s_all = new(Load);

    public static Bundle OpenRgb => s_all.Value["openrgb"];

    public static Bundle Icue => s_all.Value["icue"];

    /// <summary>Where the Full installer puts the archives.</summary>
    public static string BundledDirectory => Path.Combine(BridgePaths.InstallDirectory, "bundled");

    /// <summary>Where downloaded archives are kept so a reinstall does not fetch them again.</summary>
    public static string DownloadDirectory => Path.Combine(BridgePaths.DataDirectory, "downloads");

    private static IReadOnlyDictionary<string, Bundle> Load()
    {
        using var stream = typeof(Bundles).Assembly.GetManifestResourceStream("ChromaBridge.bundles.json")
            ?? throw new InvalidOperationException("bundles.json is not embedded in the build");

        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            var value = property.Value;

            result[property.Name] = new Bundle(
                property.Name,
                value.GetProperty("version").GetString() ?? "",
                value.GetProperty("fileName").GetString() ?? "",
                value.GetProperty("url").GetString() ?? "",
                (value.GetProperty("sha256").GetString() ?? "").ToLowerInvariant());
        }

        return result;
    }
}

/// <summary>
/// Finds a bundle's archive: the copy the Full installer shipped, a copy downloaded earlier,
/// or a fresh download. Whatever the source, the archive is used only if its SHA-256 matches
/// the pinned one.
/// </summary>
public static class BundleStore
{
    /// <summary>
    /// The path of a verified archive, or null when none could be had. <paramref name="status"/>
    /// receives progress text for the user; failures are logged.
    /// </summary>
    public static string? Obtain(Bundle bundle, Action<string> status, CancellationToken cancellationToken)
    {
        var bundled = Path.Combine(Bundles.BundledDirectory, bundle.FileName);

        if (File.Exists(bundled))
        {
            if (HashMatches(bundled, bundle.Sha256))
            {
                Log.Info($"[{bundle.Id}] Using the bundled {bundle.FileName}");
                return bundled;
            }

            Log.Warn($"[{bundle.Id}] The bundled {bundle.FileName} does not match its expected SHA-256; ignoring it");
        }

        var downloaded = Path.Combine(Bundles.DownloadDirectory, bundle.FileName);

        if (File.Exists(downloaded))
        {
            if (HashMatches(downloaded, bundle.Sha256))
                return downloaded;

            File.Delete(downloaded);
        }

        var partial = downloaded + ".download";

        try
        {
            Directory.CreateDirectory(Bundles.DownloadDirectory);

            status($"Downloading {bundle.Id switch { "openrgb" => "OpenRGB", "icue" => "the iCUE SDK", _ => bundle.Id }} {bundle.Version}...");
            Log.Info($"[{bundle.Id}] Downloading {bundle.Url}");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = http.GetAsync(bundle.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();

                using var file = File.Create(partial);

                response.Content.CopyToAsync(file, cancellationToken).GetAwaiter().GetResult();
            }

            if (!HashMatches(partial, bundle.Sha256))
            {
                File.Delete(partial);
                Log.Error($"[{bundle.Id}] The download of {bundle.FileName} did not match its expected SHA-256 and was discarded");
                return null;
            }

            File.Move(partial, downloaded, overwrite: true);

            return downloaded;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"[{bundle.Id}] Downloading {bundle.FileName} failed", ex);
            return null;
        }
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        try
        {
            using var file = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(file)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
