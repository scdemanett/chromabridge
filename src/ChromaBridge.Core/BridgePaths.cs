// Licensed under the MIT license. See LICENSE.txt in the project root.

namespace ChromaBridge;

/// <summary>
/// Names and locations shared by the service, the provider hosts and the tray app.
/// </summary>
public static class BridgePaths
{
    public const string ProductName = "ChromaBridge";

    /// <summary>
    /// The GitHub repository ("owner/name") whose releases the updater installs. Installed
    /// copies keep asking this address, so do not change it without leaving a redirect.
    /// </summary>
    public const string UpdateRepository = "scdemanett/chromabridge";

    /// <summary>The named pipe the tray app uses to talk to the service.</summary>
    public const string ControlPipeName = "ChromaBridge.Control";

    /// <summary>Signaled to ask a running service to shut down cleanly.</summary>
    public const string ServiceStopEventName = "ChromaBridge.Service.Stop";

    public const string ServiceMutexName = "ChromaBridge.Service";

    public const string AppMutexName = "ChromaBridge.App";

    public const string ServiceExeName = "ChromaBridge.Service.exe";

    public const string HostExeName = "ChromaBridge.Host.exe";

    public const string AppExeName = "ChromaBridge.exe";

    /// <summary>
    /// Settings and logs live under ProgramData so the elevated service and the
    /// unelevated tray app see the same files.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Optional file that overrides the built-in Razer app IDs.</summary>
    public static string AppIdOverrideFile => Path.Combine(DataDirectory, "appids.json");

    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Written by the installer: "full" (third-party downloads bundled) or "lite" (fetched on
    /// first use). The updater installs the same edition. Missing means full, which is what
    /// installers before the split shipped.
    /// </summary>
    public static string Edition
    {
        get
        {
            try
            {
                var path = Path.Combine(InstallDirectory, "edition.txt");

                return File.Exists(path) && File.ReadAllText(path).Trim().Equals("lite", StringComparison.OrdinalIgnoreCase) ? "lite" : "full";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "full";
            }
        }
    }

    public static string ServiceExePath => Path.Combine(InstallDirectory, ServiceExeName);

    public static string HostExePath => Path.Combine(InstallDirectory, HostExeName);
}
