// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace ChromaControl.Hosting
{
    /// <summary>
    /// Where and how the current module is running
    /// </summary>
    public static class ModuleEnvironment
    {
        /// <summary>
        /// The error GetCurrentPackageFullName returns for a process with no package identity
        /// </summary>
        private const int AppModelErrorNoPackage = 15700;

        /// <summary>
        /// Gets the package full name for the calling process
        /// </summary>
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[] packageFullName);

        /// <summary>
        /// The module name, for example ChromaControl.Asus
        /// </summary>
        public static string ModuleName { get; } = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location);

        /// <summary>
        /// If the module runs from the installed MSIX package rather than as a plain exe
        /// </summary>
        public static bool IsPackaged { get; } = DetectPackaged();

        /// <summary>
        /// The name of the event that asks a running module to shut down cleanly
        /// </summary>
        public static string StopEventName => $"{ModuleName}.Stop";

        /// <summary>
        /// If a console window should be shown
        /// </summary>
        public static bool DebugMode
        {
            get
            {
                if (HasArgument("--console"))
                    return true;

                if (!IsPackaged)
                    return false;

                var debugMode = ApplicationData.Current.LocalSettings.Values["DebugMode"];

                return debugMode != null && (bool)debugMode;
            }
        }

        /// <summary>
        /// The folder log files are written to
        /// </summary>
        public static string LogFolder
        {
            get
            {
                var root = IsPackaged
                    ? ApplicationData.Current.LocalFolder.Path
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromaControl");

                return Path.Combine(root, "logs");
            }
        }

        /// <summary>
        /// The log file of the current module
        /// </summary>
        public static string LogFile => Path.Combine(LogFolder, $"{ModuleName}.log");

        /// <summary>
        /// Checks if the module was started with a command line argument
        /// </summary>
        /// <param name="argument">The argument</param>
        /// <returns>If the argument was passed</returns>
        public static bool HasArgument(string argument)
        {
            return Environment.GetCommandLineArgs().Skip(1).Any(x => string.Equals(x, argument, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Detects if the process has package identity
        /// </summary>
        /// <returns>If the process is packaged</returns>
        private static bool DetectPackaged()
        {
            var length = 0;

            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
    }
}
