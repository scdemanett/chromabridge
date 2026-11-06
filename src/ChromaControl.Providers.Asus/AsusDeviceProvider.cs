// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AuraServiceLib;
using ChromaControl.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChromaControl.Providers.Asus
{
    /// <summary>
    /// The Asus device provider
    /// </summary>
    public class AsusDeviceProvider : IDeviceProvider
    {
        /// <summary>
        /// The first delay between detection attempts
        /// </summary>
        private static readonly TimeSpan s_initialRetryDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The longest delay between detection attempts
        /// </summary>
        private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The Aura device types, tried one by one when enumerating every type at once finds nothing
        /// </summary>
        private static readonly (uint Type, string Name)[] s_deviceTypes =
        {
            (0x00010000, "Motherboard"),
            (0x00011000, "Motherboard Addressable Strip"),
            (0x00012000, "Desktop"),
            (0x00020000, "Graphics Card"),
            (0x00030000, "Display"),
            (0x00040000, "Headset"),
            (0x00050000, "Microphone"),
            (0x00060000, "External Hard Drive"),
            (0x00061000, "External Optical Drive"),
            (0x00070000, "Memory"),
            (0x00080000, "Keyboard"),
            (0x00081000, "Notebook Keyboard"),
            (0x00081001, "Notebook Keyboard (4 Zone)"),
            (0x00082000, "ROG Strix Keyboard"),
            (0x00090000, "Mouse"),
            (0x000B0000, "Chassis"),
            (0x000C0000, "Projector")
        };

        /// <summary>
        /// The Aura device type of the AniMe Matrix display. It is a pixel display rather than
        /// lighting, so it is left for Armoury Crate to drive.
        /// </summary>
        private const uint AnimeMatrixDeviceType = 0x000F8100;

        /// <summary>
        /// The provider name
        /// </summary>
        public string Name => "Asus";

        /// <summary>
        /// The devices the provider has
        /// </summary>
        public IEnumerable<IDevice> Devices => _devices;

        /// <summary>
        /// The devices the provider has
        /// </summary>
        private readonly List<AsusDevice> _devices;

        /// <summary>
        /// The provider sdk, created on first use
        /// </summary>
        private IAuraSdk _sdk;

        /// <summary>
        /// If the provider is in exclusive mode
        /// </summary>
        private bool _inExclusiveMode;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<AsusDeviceProvider> _logger;

        /// <summary>
        /// Creates an Asus device provider
        /// </summary>
        /// <param name="logger">The logger</param>
        public AsusDeviceProvider(ILogger<AsusDeviceProvider> logger)
        {
            _devices = new List<AsusDevice>();
            _inExclusiveMode = false;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the provider, blocking until Aura reports at least one device
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait for devices</param>
        public void Initialize(CancellationToken cancellationToken)
        {
            WaitForLightingService(cancellationToken);

            var delay = s_initialRetryDelay;

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (TryDetectDevices(attempt))
                        return;
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "Detection attempt {Attempt}: the Aura SDK call failed (HRESULT 0x{HResult:X8})", attempt, ex.HResult);

                    ResetSdk();
                }

                _logger.LogInformation("Detection attempt {Attempt}: no Aura devices yet, retrying in {Delay} seconds", attempt, delay.TotalSeconds);

                if (cancellationToken.WaitHandle.WaitOne(delay))
                    cancellationToken.ThrowIfCancellationRequested();

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, s_maxRetryDelay.Ticks));
            }
        }

        /// <summary>
        /// Performs a health check on the provider
        /// </summary>
        public void PerformHealthCheck()
        {
            WaitForLightingService(CancellationToken.None);
        }

        /// <summary>
        /// Requests exclusive control over the provider
        /// </summary>
        public void RequestControl()
        {
            if (_inExclusiveMode)
                return;

            GetSdk().SwitchMode();
            _inExclusiveMode = true;

            _logger.LogInformation("Requested control of Aura lighting (the SDK does not report whether Armoury Crate granted it)");
        }

        /// <summary>
        /// Releases exclusive control over the provider
        /// </summary>
        public void ReleaseControl()
        {
            if (!_inExclusiveMode || _sdk == null)
                return;

            try
            {
                if (_sdk is IAuraSdk2 sdk2)
                    sdk2.ReleaseControl(0);
                else
                    _sdk.SwitchMode();

                _logger.LogInformation("Released control of Aura lighting");
            }
            catch (COMException ex)
            {
                _logger.LogError(ex, "Failed releasing control of Aura lighting (HRESULT 0x{HResult:X8})", ex.HResult);
            }
            finally
            {
                _inExclusiveMode = false;
            }
        }

        /// <summary>
        /// Waits for the Asus lighting service to be running
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait</param>
        private void WaitForLightingService(CancellationToken cancellationToken)
        {
            var logged = false;

            while (!IsLightingServiceRunning())
            {
                if (!logged)
                {
                    _logger.LogInformation("Waiting for the Asus LightingService process...");
                    logged = true;
                }

                if (cancellationToken.WaitHandle.WaitOne(1000))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            _logger.LogInformation("Asus LightingService is running");
        }

        /// <summary>
        /// Checks if the Asus lighting service is running
        /// </summary>
        /// <returns>If it is running</returns>
        private static bool IsLightingServiceRunning()
        {
            var processes = Process.GetProcessesByName("LightingService");

            foreach (var process in processes)
                process.Dispose();

            return processes.Length != 0;
        }

        /// <summary>
        /// Runs one detection attempt. Control is kept only when devices were found.
        /// </summary>
        /// <param name="attempt">The attempt number</param>
        /// <returns>If devices were found</returns>
        private bool TryDetectDevices(int attempt)
        {
            var sdk = GetSdk();

            // Informational only: what Aura reports before control is taken
            var before = Enumerate(sdk, 0, "All");
            _logger.LogInformation("Detection attempt {Attempt}: Enumerate(0) before taking control returned {Count} device(s)", attempt, before.Count);

            // Asus' own SDK samples take control before enumerating, so an empty list above is
            // not conclusive. Take control, look again, and hand control straight back if
            // there is still nothing to drive.
            RequestControl();

            var found = Enumerate(sdk, 0, "All");
            _logger.LogInformation("Detection attempt {Attempt}: Enumerate(0) after taking control returned {Count} device(s)", attempt, found.Count);

            if (found.Count == 0)
            {
                foreach (var (type, typeName) in s_deviceTypes)
                {
                    var typed = Enumerate(sdk, type, typeName);

                    if (typed.Count > 0)
                        _logger.LogInformation("Detection attempt {Attempt}: Enumerate(0x{Type:X8} {TypeName}) returned {Count} device(s)", attempt, type, typeName, typed.Count);

                    found.AddRange(typed);
                }

                _logger.LogInformation("Detection attempt {Attempt}: enumerating each device type separately returned {Count} device(s) in total", attempt, found.Count);
            }

            if (found.Count == 0)
            {
                ReleaseControl();
                return false;
            }

            _devices.Clear();

            foreach (var device in found)
            {
                if (IsExcluded(device))
                {
                    _logger.LogInformation("Aura device skipped (AniMe Matrix display): {Device}", device.Name);
                    continue;
                }

                var asusDevice = new AsusDevice(device, _logger);

                _logger.LogInformation("Aura device: {Description}", asusDevice.Describe());

                _devices.Add(asusDevice);
            }

            if (_devices.Count == 0)
            {
                ReleaseControl();
                return false;
            }

            if (_devices.All(x => x.NumberOfLights == 0))
                _logger.LogWarning("Aura reported devices, but none of them has any lights");

            return true;
        }

        /// <summary>
        /// Checks if a device should be left alone
        /// </summary>
        /// <param name="device">The device</param>
        /// <returns>If the device is excluded</returns>
        private static bool IsExcluded(IAuraSyncDevice device)
        {
            try
            {
                return device.Type == AnimeMatrixDeviceType || device.Name.Contains("Matrix", StringComparison.OrdinalIgnoreCase);
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>
        /// Enumerates the Aura devices of one type
        /// </summary>
        /// <param name="sdk">The sdk</param>
        /// <param name="type">The Aura device type, 0 for every type</param>
        /// <param name="typeName">The device type name, for logging</param>
        /// <returns>The devices</returns>
        private List<IAuraSyncDevice> Enumerate(IAuraSdk sdk, uint type, string typeName)
        {
            var result = new List<IAuraSyncDevice>();

            try
            {
                var collection = sdk.Enumerate(type);

                if (collection == null)
                {
                    _logger.LogDebug("Enumerate(0x{Type:X8} {TypeName}) returned null", type, typeName);
                    return result;
                }

                foreach (IAuraSyncDevice device in collection)
                    result.Add(device);

                _logger.LogDebug("Enumerate(0x{Type:X8} {TypeName}) returned {Count} device(s)", type, typeName, result.Count);
            }
            catch (COMException ex) when (type != 0)
            {
                // A failure for one specific type should not stop the other types being tried
                _logger.LogDebug("Enumerate(0x{Type:X8} {TypeName}) failed with HRESULT 0x{HResult:X8}: {Message}", type, typeName, ex.HResult, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Gets the Aura sdk, creating it on first use
        /// </summary>
        /// <returns>The sdk</returns>
        private IAuraSdk GetSdk()
        {
            if (_sdk == null)
            {
                _sdk = new AuraSdk();

                _logger.LogInformation("Created the Aura SDK object (ReleaseControl supported: {SupportsRelease})", _sdk is IAuraSdk2);
            }

            return _sdk;
        }

        /// <summary>
        /// Drops the Aura sdk object after a failure so the next attempt starts clean
        /// </summary>
        private void ResetSdk()
        {
            ReleaseControl();

            if (_sdk != null && Marshal.IsComObject(_sdk))
                Marshal.FinalReleaseComObject(_sdk);

            _sdk = null;
            _inExclusiveMode = false;
        }
    }
}
