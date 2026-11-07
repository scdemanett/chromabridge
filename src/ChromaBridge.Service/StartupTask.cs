// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using ChromaBridge.Service.OpenRgb;

namespace ChromaBridge.Service;

/// <summary>
/// The scheduled task that starts the helper when the user logs in. A task is the one way to
/// start an elevated process at login without a UAC prompt: it is registered once, from an
/// elevated process, to run with the highest privileges the user has.
/// </summary>
internal static class StartupTask
{
    public const string TaskName = "ChromaBridge Helper";

    /// <summary>
    /// Creates or replaces the task. Must be called from an elevated process.
    /// </summary>
    /// <param name="userSid">
    /// The user the task runs for. When null, the user who owns the desktop (the one who is
    /// logged in), not the account that elevated: with over-the-shoulder elevation those
    /// differ, and the task must start for the person using the PC.
    /// </param>
    public static bool Register(string? userSid)
    {
        SecurityIdentifier sid;

        try
        {
            if (!string.IsNullOrWhiteSpace(userSid))
            {
                sid = new SecurityIdentifier(userSid);
            }
            else
            {
                using var identity = WindowsIdentity.GetCurrent();

                sid = UnelevatedLauncher.ShellUser() ?? identity.User!;
            }
        }
        catch (ArgumentException)
        {
            Log.Error($"'{userSid}' is not a valid user SID");
            return false;
        }

        // The definition is read back by Task Scheduler, which runs it with the highest
        // privileges at every logon, so it is written where only administrators can write
        var xmlPath = Path.Combine(BridgePaths.DataDirectory, "logon-task.xml");

        try
        {
            // Task Scheduler expects UTF-16
            File.WriteAllText(xmlPath, CreateDefinition(sid, BridgePaths.ServiceExePath), Encoding.Unicode);

            var exitCode = RunSchtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");

            Log.Info(exitCode == 0
                ? $"Registered the logon task for {sid} -> {BridgePaths.ServiceExePath}"
                : $"Registering the logon task failed (schtasks exit code {exitCode})");

            return exitCode == 0;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
            }
        }
    }

    public static bool Unregister()
    {
        RemoveTrayRunEntry();

        if (!IsRegistered())
            return true;

        var exitCode = RunSchtasks("/Delete", "/TN", TaskName, "/F");

        Log.Info(exitCode == 0 ? "Removed the logon task" : $"Removing the logon task failed (schtasks exit code {exitCode})");

        return exitCode == 0;
    }

    /// <summary>
    /// Removes the tray app's Run entry from the logged-in user's hive and from this
    /// process's own. The installer writes it as the logged-in user; at uninstall this runs
    /// elevated, possibly as a different account, so HKCU alone could miss it.
    /// </summary>
    private static void RemoveTrayRunEntry()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (UnelevatedLauncher.ShellUser() is { } shellUser)
            sids.Add(shellUser.Value);

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User != null)
                sids.Add(identity.User.Value);
        }

        foreach (var sid in sids)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.Users.OpenSubKey(sid + "\\" + runKey, writable: true);

                if (key?.GetValue(BridgePaths.ProductName) != null)
                {
                    key.DeleteValue(BridgePaths.ProductName, throwOnMissingValue: false);
                    Log.Info($"Removed the tray app's Run entry for {sid}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Error($"Could not remove the tray app's Run entry for {sid}", ex);
            }
        }
    }

    public static bool IsRegistered() => RunSchtasks("/Query", "/TN", TaskName) == 0;

    private static string CreateDefinition(SecurityIdentifier user, string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>ChromaBridge</Author>
            <Description>Starts the ChromaBridge helper at login so lighting follows Razer Chroma. It runs with administrator rights because ASUS only reports devices to an administrator.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{user.Value}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{user.Value}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath))}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static int RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)!;

            // Drain both streams so a full pipe cannot block the exit
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return -1;
            }

            Task.WaitAll(output, error);

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not run schtasks.exe", ex);
            return -1;
        }
    }
}
