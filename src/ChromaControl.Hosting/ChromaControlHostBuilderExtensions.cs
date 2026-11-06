// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;

namespace ChromaControl.Hosting
{
    /// <summary>
    /// Chroma Control host builder extensions
    /// </summary>
    public static class ChromaControlHostBuilderExtensions
    {
        /// <summary>
        /// Allocates a console window
        /// </summary>
        [DllImport("kernel32")]
        private static extern void AllocConsole();

        /// <summary>
        /// Configures the host builder for Chroma Control
        /// </summary>
        /// <param name="hostBuilder">The host builder</param>
        /// <returns>The host builder</returns>
        public static IHostBuilder UseChromaControl(this IHostBuilder hostBuilder)
        {
            if (ModuleEnvironment.DebugMode && !ModuleEnvironment.HasArgument("--stop"))
            {
                AllocConsole();
                Console.Title = ModuleEnvironment.ModuleName;
            }

            Directory.CreateDirectory(ModuleEnvironment.LogFolder);

            return hostBuilder
                .UseContentRoot(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location))
                .ConfigureLogging((hostingContext, logging) =>
                {
                    logging.AddFilter("ChromaControl", LogLevel.Debug);

                    logging.AddFile(ModuleEnvironment.LogFile, options =>
                    {
                        options.Append = true;
                        options.MinLevel = LogLevel.Debug;
                        options.FileSizeLimitBytes = 5 * 1024 * 1024;
                        options.MaxRollingFiles = 3;

                        // If the log file is locked, write to a second file instead of failing
                        options.HandleFileError = error => error.UseNewLogFileName(Path.Combine(ModuleEnvironment.LogFolder, $"{ModuleEnvironment.ModuleName}.{Environment.ProcessId}.log"));
                    });
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<ModuleService>();
                });
        }

        /// <summary>
        /// Builds and runs the host with a mutex
        /// </summary>
        /// <param name="hostBuilder">The host builder</param>
        public static void RunWithMutex(this IHostBuilder hostBuilder)
        {
            // "--stop" asks an already running module to shut down cleanly, then exits
            if (ModuleEnvironment.HasArgument("--stop"))
            {
                if (EventWaitHandle.TryOpenExisting(ModuleEnvironment.StopEventName, out var existingStopEvent))
                {
                    using (existingStopEvent)
                        existingStopEvent.Set();
                }

                return;
            }

            var mutex = new ModuleMutex();

            if (!mutex.Success)
            {
                File.AppendAllText(
                    Path.Combine(ModuleEnvironment.LogFolder, $"{ModuleEnvironment.ModuleName}.startup.log"),
                    $"{DateTime.Now:O} Another {ModuleEnvironment.ModuleName} is already running, so this one exited.{Environment.NewLine}");

                return;
            }

            using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ModuleEnvironment.StopEventName);

            using var host = hostBuilder.ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton(mutex);
            }).Build();

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            var stopRegistration = ThreadPool.RegisterWaitForSingleObject(stopEvent, (state, timedOut) => lifetime.StopApplication(), null, Timeout.Infinite, true);

            try
            {
                host.Run();
            }
            finally
            {
                stopRegistration.Unregister(null);
            }
        }
    }
}
