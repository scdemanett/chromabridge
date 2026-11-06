// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Threading;
using ChromaBroadcast;
using ChromaControl.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChromaControl.Hosting
{
    /// <summary>
    /// Applies Chroma effect frames to one device on its own thread, so a slow device does not
    /// hold back the others. Only the newest frame is kept; frames that arrive while the device
    /// is still busy are dropped.
    /// </summary>
    internal sealed class DeviceWorker
    {
        /// <summary>
        /// The device
        /// </summary>
        private readonly IDevice _device;

        /// <summary>
        /// Which of the five Chroma Link colors the first light of the device gets
        /// </summary>
        private readonly int _colorOffset;

        /// <summary>
        /// Gets the color correction in use
        /// </summary>
        private readonly Func<ColorCorrection> _colorCorrection;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Signals that a frame is waiting
        /// </summary>
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        /// <summary>
        /// The newest frame waiting to be applied, boxed
        /// </summary>
        private object _pending;

        /// <summary>
        /// The thread applying frames
        /// </summary>
        private Thread _thread;

        /// <summary>
        /// The number of frames applied
        /// </summary>
        private long _applied;

        /// <summary>
        /// The time spent applying frames, in stopwatch ticks
        /// </summary>
        private long _applyTicks;

        /// <summary>
        /// The number of frames that failed to apply
        /// </summary>
        private long _failures;

        /// <summary>
        /// The device name
        /// </summary>
        public string Name => _device.Name;

        /// <summary>
        /// The number of frames applied
        /// </summary>
        public long Applied => Interlocked.Read(ref _applied);

        /// <summary>
        /// The time spent applying frames, in stopwatch ticks
        /// </summary>
        public long ApplyTicks => Interlocked.Read(ref _applyTicks);

        /// <summary>
        /// The number of frames that failed to apply
        /// </summary>
        public long Failures => Interlocked.Read(ref _failures);

        /// <summary>
        /// Creates a device worker
        /// </summary>
        /// <param name="device">The device</param>
        /// <param name="colorOffset">Which of the five Chroma Link colors the first light gets</param>
        /// <param name="colorCorrection">Gets the color correction in use</param>
        /// <param name="logger">The logger</param>
        public DeviceWorker(IDevice device, int colorOffset, Func<ColorCorrection> colorCorrection, ILogger logger)
        {
            _device = device;
            _colorOffset = colorOffset;
            _colorCorrection = colorCorrection;
            _logger = logger;
        }

        /// <summary>
        /// Starts applying frames
        /// </summary>
        /// <param name="stoppingToken">Stops the worker</param>
        public void Start(CancellationToken stoppingToken)
        {
            _thread = new Thread(() => Run(stoppingToken)) { IsBackground = true, Name = $"Chroma Apply - {Name}" };
            _thread.Start();
        }

        /// <summary>
        /// Hands the worker a frame, replacing any frame it has not applied yet
        /// </summary>
        /// <param name="effect">The effect frame</param>
        public void Post(RzChromaBroadcastEffect effect)
        {
            Interlocked.Exchange(ref _pending, effect);
            _signal.Set();
        }

        /// <summary>
        /// Waits for the worker to finish after its stopping token was cancelled
        /// </summary>
        public void Join()
        {
            _signal.Set();
            _thread?.Join(3000);
        }

        /// <summary>
        /// Applies the newest waiting frame until the worker is stopped
        /// </summary>
        /// <param name="stoppingToken">Stops the worker</param>
        private void Run(CancellationToken stoppingToken)
        {
            var handles = new[] { _signal, stoppingToken.WaitHandle };
            var colors = new System.Drawing.Color[5];

            while (!stoppingToken.IsCancellationRequested)
            {
                WaitHandle.WaitAny(handles);

                if (stoppingToken.IsCancellationRequested)
                    break;

                if (Interlocked.Exchange(ref _pending, null) is not RzChromaBroadcastEffect effect)
                    continue;

                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var correction = _colorCorrection();

                    colors[0] = correction.Apply(effect.ChromaLink1);
                    colors[1] = correction.Apply(effect.ChromaLink2);
                    colors[2] = correction.Apply(effect.ChromaLink3);
                    colors[3] = correction.Apply(effect.ChromaLink4);
                    colors[4] = correction.Apply(effect.ChromaLink5);

                    var index = _colorOffset;

                    foreach (var light in _device.Lights)
                    {
                        light.Color = colors[index];
                        index = index == 4 ? 0 : index + 1;
                    }

                    _device.ApplyLights();

                    Interlocked.Add(ref _applyTicks, Stopwatch.GetTimestamp() - started);
                    Interlocked.Increment(ref _applied);
                }
                catch (Exception ex)
                {
                    var failures = Interlocked.Increment(ref _failures);

                    if (failures == 1 || failures % 500 == 0)
                        _logger.LogError(ex, "Failed applying a Chroma effect frame to {Device} ({Failures} failure(s) so far)", Name, failures);
                }
            }
        }
    }
}
