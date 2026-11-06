// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChromaBridge.Service.OpenRgb;

/// <summary>
/// Starts a process as the logged-in user without administrator rights, from this elevated
/// service. Third-party programs the service launches should not inherit its elevation.
///
/// It borrows the token of the desktop shell (Explorer), which runs unelevated as the
/// logged-in user, and starts the process with a copy of it.
/// </summary>
internal static class UnelevatedLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName, System.Text.StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Starts the program with no window. Returns the process, or throws
    /// <see cref="Win32Exception"/> or <see cref="InvalidOperationException"/> when it could
    /// not be started unelevated. It never falls back to starting it elevated.
    /// </summary>
    public static Process Start(string exePath, string arguments, string workingDirectory)
    {
        // Nothing to drop when the service itself is not elevated (development runs)
        if (!ProcessInfo.IsElevated)
        {
            return Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("The process did not start.");
        }

        var shellWindow = GetShellWindow();

        if (shellWindow == IntPtr.Zero)
            throw new InvalidOperationException("The desktop shell is not running, so there is no unelevated session to start the program in.");

        GetWindowThreadProcessId(shellWindow, out var shellProcessId);

        var shellProcess = IntPtr.Zero;
        var shellToken = IntPtr.Zero;
        var userToken = IntPtr.Zero;

        try
        {
            shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId);

            if (shellProcess == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the desktop shell process.");

            if (!OpenProcessToken(shellProcess, TokenDuplicate, out shellToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the desktop shell's token.");

            if (!DuplicateTokenEx(shellToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out userToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not copy the desktop shell's token.");

            var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new System.Text.StringBuilder($"\"{exePath}\" {arguments}");

            if (!CreateProcessWithTokenW(userToken, 0, null, commandLine, CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startupInfo, out var processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the program as the logged-in user.");

            try
            {
                return Process.GetProcessById(processInformation.dwProcessId);
            }
            finally
            {
                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
            }
        }
        finally
        {
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);

            if (shellToken != IntPtr.Zero)
                CloseHandle(shellToken);

            if (shellProcess != IntPtr.Zero)
                CloseHandle(shellProcess);
        }
    }
}
