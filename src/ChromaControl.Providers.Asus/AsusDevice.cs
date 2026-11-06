// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AuraServiceLib;
using ChromaControl.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChromaControl.Providers.Asus
{
    /// <summary>
    /// An Asus device
    /// </summary>
    public class AsusDevice : IDevice
    {
        /// <summary>
        /// The device name
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The lights the device has
        /// </summary>
        public IEnumerable<IDeviceLight> Lights => _lights;

        /// <summary>
        /// The lights the device has
        /// </summary>
        private readonly List<AsusDeviceLight> _lights;

        /// <summary>
        /// The number of lights on the device
        /// </summary>
        public int NumberOfLights => _lights.Count;

        /// <summary>
        /// The device
        /// </summary>
        private readonly IAuraSyncDevice _device;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<AsusDeviceProvider> _logger;

        /// <summary>
        /// The number of failed applies
        /// </summary>
        private long _applyFailures;

        /// <summary>
        /// Creates an Asus device
        /// </summary>
        /// <param name="device">The device</param>
        /// <param name="logger">The logger</param>
        internal AsusDevice(IAuraSyncDevice device, ILogger<AsusDeviceProvider> logger)
        {
            _device = device;
            _lights = new List<AsusDeviceLight>();
            _logger = logger;

            Name = ReadName();

            try
            {
                foreach (IAuraRgbLight light in _device.Lights)
                {
                    _lights.Add(new AsusDeviceLight(light));
                }
            }
            catch (COMException ex)
            {
                _logger.LogError(ex, "Failed reading the lights of {Device} (HRESULT 0x{HResult:X8})", Name, ex.HResult);
            }
        }

        /// <summary>
        /// Applies light changes to the device
        /// </summary>
        public void ApplyLights()
        {
            try
            {
                if (NumberOfLights > 0)
                    _device.Apply();
            }
            catch (COMException ex)
            {
                // This runs for every Chroma frame, so only log the first failure and then every 500th
                var failures = Interlocked.Increment(ref _applyFailures);

                if (failures == 1 || failures % 500 == 0)
                    _logger.LogError(ex, "Failed applying lights to {Device} (HRESULT 0x{HResult:X8}, {Failures} failure(s) so far)", Name, ex.HResult, failures);
            }
        }

        /// <summary>
        /// Describes the device for the log
        /// </summary>
        /// <returns>The description</returns>
        internal string Describe()
        {
            try
            {
                return $"\"{Name}\" type 0x{_device.Type:X8}, {NumberOfLights} light(s), layout {_device.Width}x{_device.Height}";
            }
            catch (COMException ex)
            {
                return $"\"{Name}\" {NumberOfLights} light(s) (reading the device details failed: 0x{ex.HResult:X8})";
            }
        }

        /// <summary>
        /// Reads the device name
        /// </summary>
        /// <returns>The device name</returns>
        private string ReadName()
        {
            try
            {
                return _device.Name;
            }
            catch (COMException)
            {
                return "Unknown Aura Device";
            }
        }
    }
}
