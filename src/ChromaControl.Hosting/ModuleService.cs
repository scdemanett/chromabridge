// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChromaBroadcast;
using ChromaControl.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChromaControl.Hosting
{
    /// <summary>
    /// A Chroma Control module service
    /// </summary>
    public class ModuleService : BackgroundService
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<ModuleService> _logger;

        /// <summary>
        /// The device provider
        /// </summary>
        private readonly IDeviceProvider _deviceProvider;

        /// <summary>
        /// The color correction in use, replaced whenever the settings file changes
        /// </summary>
        private volatile ColorCorrection _colorCorrection;

        /// <summary>
        /// The lighting options
        /// </summary>
        private readonly IOptionsMonitor<LightingOptions> _lightingOptions;

        /// <summary>
        /// One worker per device being driven. Empty until the devices are known.
        /// </summary>
        private volatile DeviceWorker[] _workers = Array.Empty<DeviceWorker>();

        /// <summary>
        /// The number of effect frames received from Chroma
        /// </summary>
        private long _framesReceived;

        /// <summary>
        /// The number of effect frames that failed to apply
        /// </summary>
        private long _framesFailed;

        /// <summary>
        /// Creates the module service
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="deviceProvider">The device provider</param>
        /// <param name="lightingOptions">The lighting options</param>
        public ModuleService(ILogger<ModuleService> logger, IDeviceProvider deviceProvider, IOptionsMonitor<LightingOptions> lightingOptions)
        {
            _logger = logger;
            _deviceProvider = deviceProvider;
            _lightingOptions = lightingOptions;

            SetColorCorrection(lightingOptions.CurrentValue);
            lightingOptions.OnChange(SetColorCorrection);
        }

        /// <summary>
        /// Replaces the color correction
        /// </summary>
        /// <param name="options">The lighting options</param>
        private void SetColorCorrection(LightingOptions options)
        {
            var correction = new ColorCorrection(options);

            _colorCorrection = correction;

            _logger.LogInformation("Color correction: saturation {Saturation}, gamma {Gamma}, brightness {Brightness}", correction.Saturation, correction.Gamma, correction.Brightness);
        }

        /// <summary>
        /// Executes the module service
        /// </summary>
        /// <param name="stoppingToken">The stopping token</param>
        /// <returns>A task</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();

            if (_deviceProvider == null)
            {
                _logger.LogError("No Device Provider Set, Cannot Initialize!!!");
                return;
            }

            var version = Assembly.GetEntryAssembly().GetName().Version.ToString(3);
            var broadcastInitialized = false;

            _logger.LogInformation("Initializing Chroma Control - {Provider} v{Version}...", _deviceProvider.Name, version);

            try
            {
                _logger.LogInformation("Initializing Device Provider...");

                // Blocks until the provider has devices, so control is never taken with nothing to drive
                _deviceProvider.Initialize(stoppingToken);

                _deviceProvider.RequestControl();

                _workers = CreateWorkers();

                foreach (var worker in _workers)
                    worker.Start(stoppingToken);

                _logger.LogInformation("Initializing Razer Chroma Broadcast SDK...");

                var initResult = RzChromaBroadcastAPI.Init(_deviceProvider.GetGuid());
                broadcastInitialized = true;

                _logger.LogInformation("Razer Chroma Broadcast Init returned {Result}", initResult);

                var registerResult = RzChromaBroadcastAPI.RegisterEventNotification(OnChromaBroadcastEvent);

                _logger.LogInformation("Razer Chroma Broadcast RegisterEventNotification returned {Result}", registerResult);

                if (initResult == RzResult.Success && registerResult == RzResult.Success)
                    _logger.LogInformation("Chroma Control - {Provider} Started Succesfully!", _deviceProvider.Name);
                else
                    _logger.LogError("Chroma Control - {Provider} could not connect to Razer Chroma Broadcast", _deviceProvider.Name);

                long lastFrames = 0;
                var lastApplied = new long[_workers.Length];
                var lastTicks = new long[_workers.Length];

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(30000, stoppingToken);

                    var frames = Interlocked.Read(ref _framesReceived);

                    _logger.LogDebug("Chroma effect frames in the last 30 seconds: {Received} received ({Rate:F1} per second)", frames - lastFrames, (frames - lastFrames) / 30.0);

                    for (var i = 0; i < _workers.Length; i++)
                    {
                        var applied = _workers[i].Applied;
                        var ticks = _workers[i].ApplyTicks;
                        var recent = applied - lastApplied[i];
                        var averageMs = recent > 0 ? (ticks - lastTicks[i]) * 1000.0 / Stopwatch.Frequency / recent : 0;

                        _logger.LogDebug("  {Device}: {Applied} applied ({Rate:F1} per second), {AverageMs:F1} ms each, {Failed} failed in total", _workers[i].Name, recent, recent / 30.0, averageMs, _workers[i].Failures);

                        lastApplied[i] = applied;
                        lastTicks[i] = ticks;
                    }

                    lastFrames = frames;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Stopping Chroma Control - {Provider}...", _deviceProvider.Name);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Chroma Control - {Provider} failed", _deviceProvider.Name);
            }
            finally
            {
                Shutdown(broadcastInitialized);
            }
        }

        /// <summary>
        /// Disconnects from Chroma and hands lighting control back
        /// </summary>
        /// <param name="broadcastInitialized">If the Chroma Broadcast SDK was initialized</param>
        private void Shutdown(bool broadcastInitialized)
        {
            if (broadcastInitialized)
            {
                try
                {
                    RzChromaBroadcastAPI.UnRegisterEventNotification();
                    RzChromaBroadcastAPI.UnInit();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed shutting down the Razer Chroma Broadcast SDK");
                }
            }

            // The workers stop with the stopping token; wait so none is mid-write when control is released
            foreach (var worker in _workers)
                worker.Join();

            try
            {
                _deviceProvider.ReleaseControl();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed releasing control of the {Provider} provider", _deviceProvider.Name);
            }

            _logger.LogInformation("Chroma Control - {Provider} Stopped", _deviceProvider.Name);
        }

        /// <summary>
        /// Occurs when a callback is recieved from the Chroma Broadcast API
        /// </summary>
        /// <param name="type">The callback type</param>
        /// <param name="status">Status data</param>
        /// <param name="effect">Effect data</param>
        /// <returns>A razer result</returns>
        private RzResult OnChromaBroadcastEvent(RzChromaBroadcastType type, RzChromaBroadcastStatus? status, RzChromaBroadcastEffect? effect)
        {
            // This is called from native code: an exception must never escape it
            try
            {
                if (type == RzChromaBroadcastType.BroadcastEffect && effect.HasValue)
                {
                    var frame = Interlocked.Increment(ref _framesReceived);

                    if (frame == 1)
                    {
                        _logger.LogInformation("First Chroma effect frame received: {Color1} {Color2} {Color3} {Color4} {Color5}",
                            ToHex(effect.Value.ChromaLink1), ToHex(effect.Value.ChromaLink2), ToHex(effect.Value.ChromaLink3), ToHex(effect.Value.ChromaLink4), ToHex(effect.Value.ChromaLink5));
                    }

                    // Hand the frame to the device workers so the callback returns immediately
                    foreach (var worker in _workers)
                        worker.Post(effect.Value);
                }
                else if (type == RzChromaBroadcastType.BroadcastStatus)
                {
                    if (status == RzChromaBroadcastStatus.Live)
                        _logger.LogInformation("Razer Chroma Broadcast API Connected");
                    else if (status == RzChromaBroadcastStatus.NotLive)
                        _logger.LogInformation("Razer Chroma Broadcast API Disconnected");
                }
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref _framesFailed);

                if (failures == 1 || failures % 500 == 0)
                    _logger.LogError(ex, "Failed handling a Chroma Broadcast event ({Failures} failure(s) so far)", failures);
            }

            return RzResult.Success;
        }

        /// <summary>
        /// Creates one worker per device, leaving out devices excluded in the settings
        /// </summary>
        /// <returns>The workers</returns>
        private DeviceWorker[] CreateWorkers()
        {
            var excluded = _lightingOptions.CurrentValue.ExcludedDevices ?? Array.Empty<string>();
            var workers = new List<DeviceWorker>();
            var lightIndex = 0;

            foreach (var device in _deviceProvider.Devices)
            {
                var lights = device.Lights.Count();

                if (excluded.Any(x => string.Equals(x?.Trim(), device.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Skipped Device (excluded in settings): {Device} - {Lights} Lights", device.Name, lights);
                    continue;
                }

                _logger.LogInformation("Found Device: {Device} - {Lights} Lights", device.Name, lights);

                // The five Chroma Link colors repeat along all lights of all devices in order
                workers.Add(new DeviceWorker(device, lightIndex % 5, () => _colorCorrection, _logger));

                lightIndex += lights;
            }

            return workers.ToArray();
        }

        /// <summary>
        /// Formats a color for the log
        /// </summary>
        /// <param name="color">The color</param>
        /// <returns>The color as #RRGGBB</returns>
        private static string ToHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }
}
