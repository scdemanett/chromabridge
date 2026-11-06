// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChromaBroadcast;
using ChromaControl.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        public ModuleService(ILogger<ModuleService> logger, IDeviceProvider deviceProvider)
        {
            _logger = logger;
            _deviceProvider = deviceProvider;
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

                foreach (var device in _deviceProvider.Devices)
                {
                    _logger.LogInformation("Found Device: {Device} - {Lights} Lights", device.Name, device.Lights.Count());
                }

                _deviceProvider.RequestControl();

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

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(30000, stoppingToken);

                    var frames = Interlocked.Read(ref _framesReceived);

                    _logger.LogDebug("Chroma effect frames: {Total} total, {Recent} in the last 30 seconds, {Failed} failed", frames, frames - lastFrames, Interlocked.Read(ref _framesFailed));

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

                    ApplyEffect(effect.Value);
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
        /// Applies a Chroma effect to every light of every device
        /// </summary>
        /// <param name="effect">The effect</param>
        private void ApplyEffect(RzChromaBroadcastEffect effect)
        {
            var currentColor = 0;

            foreach (var device in _deviceProvider.Devices)
            {
                foreach (var light in device.Lights)
                {
                    var color = currentColor switch
                    {
                        0 => effect.ChromaLink1,
                        1 => effect.ChromaLink2,
                        2 => effect.ChromaLink3,
                        3 => effect.ChromaLink4,
                        4 => effect.ChromaLink5,
                        _ => Color.FromArgb(0, 0, 0)
                    };

                    if (currentColor == 4)
                        currentColor = 0;
                    else
                        currentColor++;

                    light.Color = color;
                }

                device.ApplyLights();
            }
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
