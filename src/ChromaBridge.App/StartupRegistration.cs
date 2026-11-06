// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace ChromaBridge.App;

/// <summary>
/// Turns "start at login" on or off. Two things start at login: the helper, through a
/// scheduled task only an administrator can create, and this tray app, through the user's
/// own Run key.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Returns false when the change was not made, for example because the UAC prompt was declined.
    /// </summary>
    public static bool Apply(bool startAtLogin)
    {
        if (!RunHelperElevated(startAtLogin))
            return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            if (startAtLogin)
                key.SetValue(BridgePaths.ProductName, $"\"{Path.Combine(BridgePaths.InstallDirectory, BridgePaths.AppExeName)}\" --tray");
            else
                key.DeleteValue(BridgePaths.ProductName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The helper still starts at login; only the tray icon will not
        }

        return true;
    }

    private static bool RunHelperElevated(bool register)
    {
        // Pass this user's SID: with over-the-shoulder elevation the elevated process runs
        // as a different account, and the task must be for the person at the keyboard.
        using var identity = WindowsIdentity.GetCurrent();

        var arguments = register ? $"--register-startup --user {identity.User?.Value}" : "--unregister-startup";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(BridgePaths.ServiceExePath, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });

            if (process == null)
                return false;

            return process.WaitForExit(20000) && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt
            return false;
        }
    }
}
