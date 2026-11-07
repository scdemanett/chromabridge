// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Security.AccessControl;
using System.Security.Principal;

namespace ChromaBridge.Service;

/// <summary>
/// The data folder under ProgramData holds the settings and logs the elevated helper reads
/// and writes. Only administrators may change it; everyone else may read. Without this, a
/// normal user could edit files an elevated process trusts.
///
/// ProgramData lets any user create folders, so the folder is trusted only if this code made
/// it or an administrator owns it already. One that someone else created first, or that is a
/// junction or symbolic link pointing elsewhere, is refused rather than taken over: applying
/// administrator ownership through a link would hand that user control of whatever the link
/// points at.
/// </summary>
internal static class DataDirectory
{
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Creates the folder if needed and restricts it. Returns false when the folder cannot be
    /// trusted, in which case the caller must not use it. Always true when not elevated, since
    /// an unelevated run could not have protected it anyway.
    /// </summary>
    public static bool Secure()
    {
        if (!ProcessInfo.IsElevated)
            return true;

        var path = BridgePaths.DataDirectory;

        try
        {
            var directory = new DirectoryInfo(path);

            if (directory.Exists)
            {
                if (!IsTrustworthy(directory, out var reason))
                {
                    // The log lives in that folder, so this cannot be logged there
                    Console.Error.WriteLine($"Refusing to use {path}: {reason}. Remove it as an administrator and start ChromaBridge again.");
                    return false;
                }
            }
            else
            {
                // Created with its permissions in one step, so there is no moment when the
                // folder exists with ProgramData's permissive defaults
                directory.Create(RestrictedSecurity());
                directory.Refresh();
            }

            directory.SetAccessControl(RestrictedSecurity());

            // Files made before the folder was restricted keep their old rules; reset them so
            // they inherit. Links inside are skipped for the same reason as above.
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Log.Warn($"Ignoring the link {entry.FullName} inside the data folder");
                    continue;
                }

                if (entry is DirectoryInfo subdirectory)
                {
                    var inherited = new DirectorySecurity();

                    inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    inherited.SetOwner(s_administrators);
                    subdirectory.SetAccessControl(inherited);
                }
                else if (entry is FileInfo file)
                {
                    var inherited = new FileSecurity();

                    inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    inherited.SetOwner(s_administrators);
                    file.SetAccessControl(inherited);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not restrict the data folder {path}: {ex.Message}");
            return false;
        }
    }

    private static bool IsTrustworthy(DirectoryInfo directory, out string reason)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            reason = "it is a junction or symbolic link";
            return false;
        }

        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        if (owner == null || (owner != s_administrators && owner != s_system))
        {
            reason = $"it is owned by {owner?.Value ?? "an unknown account"}, not by administrators";
            return false;
        }

        reason = "";
        return true;
    }

    private static DirectorySecurity RestrictedSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var security = new DirectorySecurity();

        // Drop inherited rules: ProgramData lets any user create and change files
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(s_administrators);

        security.AddAccessRule(new FileSystemAccessRule(s_administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(s_system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(s_users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        return security;
    }
}
