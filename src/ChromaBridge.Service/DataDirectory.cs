// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Security.AccessControl;
using System.Security.Principal;

namespace ChromaBridge.Service;

/// <summary>
/// The data folder under ProgramData holds the settings and logs the elevated helper reads
/// and writes. Only administrators may change it; everyone else may read. Without this, a
/// normal user could edit files an elevated process trusts.
/// </summary>
internal static class DataDirectory
{
    /// <summary>
    /// Creates the folder if needed and restricts it. Only does anything when elevated.
    /// </summary>
    public static void Secure()
    {
        if (!ProcessInfo.IsElevated)
            return;

        try
        {
            var directory = Directory.CreateDirectory(BridgePaths.DataDirectory);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var security = new DirectorySecurity();

            // Drop inherited rules: ProgramData lets any user create and change files
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

            directory.SetAccessControl(security);

            // Files made before the folder was restricted keep their old rules; reset them
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry is DirectoryInfo subdirectory)
                {
                    var inherited = new DirectorySecurity();

                    inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    inherited.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    subdirectory.SetAccessControl(inherited);
                }
                else if (entry is FileInfo file)
                {
                    var inherited = new FileSecurity();

                    inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    inherited.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    file.SetAccessControl(inherited);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Could not restrict the data folder", ex);
        }
    }
}
