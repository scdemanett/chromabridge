// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChromaBridge.Providers.Corsair;

/// <summary>
/// Gets Corsair's iCUE SDK client library in place and tells the runtime where it is. The
/// library is not part of ChromaBridge: the Full installer ships Corsair's SDK archive, the
/// Lite installer downloads it, and either way the one DLL is unpacked from it into the
/// administrator-only data folder. A development build finds the DLL beside itself instead.
/// </summary>
internal static class IcueSdk
{
    private const string LibraryName = "iCUESDK.x64_2019.dll";
    private const string ArchiveEntry = "iCUESDK/redist/x64/" + LibraryName;

    private static readonly object s_lock = new();
    private static bool s_resolverRegistered;

    private static string LibraryDirectory => Path.Combine(BridgePaths.DataDirectory, "icue", Bundles.Icue.Version);

    private static string LibraryPath => Path.Combine(LibraryDirectory, LibraryName);

    /// <summary>Makes DllImport look in the install folder first, then the unpacked copy.</summary>
    public static void RegisterResolver()
    {
        lock (s_lock)
        {
            if (s_resolverRegistered)
                return;

            NativeLibrary.SetDllImportResolver(typeof(IcueSdk).Assembly, Resolve);
            s_resolverRegistered = true;
        }
    }

    /// <summary>True once the library can be loaded, unpacking or downloading it if needed.</summary>
    public static bool Ensure(Action<string> report, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(BridgePaths.InstallDirectory, LibraryName)) || File.Exists(LibraryPath))
            return true;

        var zipPath = BundleStore.Obtain(Bundles.Icue, report, cancellationToken);

        if (zipPath == null)
            return false;

        var partial = LibraryPath + ".unpacking";

        try
        {
            Directory.CreateDirectory(LibraryDirectory);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry(ArchiveEntry)
                    ?? throw new InvalidDataException($"The iCUE SDK archive does not contain {ArchiveEntry}.");

                entry.ExtractToFile(partial, overwrite: true);
            }

            File.Move(partial, LibraryPath, overwrite: true);
            Log.Info($"[icue] Unpacked {LibraryName} {Bundles.Icue.Version} to {LibraryDirectory}");

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("[icue] Unpacking the iCUE SDK library failed", ex);
            return false;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var candidate in new[] { Path.Combine(BridgePaths.InstallDirectory, LibraryName), LibraryPath })
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
