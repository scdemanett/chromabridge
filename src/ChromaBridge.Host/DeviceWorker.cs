// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Diagnostics;
using ChromaBridge.Providers;

namespace ChromaBridge.Host;

/// <summary>
/// Writes Chroma frames to one device on its own thread. Only the newest frame is kept, so a
/// slow device drops frames instead of falling behind.
/// </summary>
internal sealed class DeviceWorker
{
    /// <summary>After this many failed writes in a row the device is treated as lost.</summary>
    private const int LostAfterFailures = 50;

    private readonly ILightingDevice _device;
    private readonly Func<ZoneMapping> _mapping;
    private readonly Func<ColorCorrection> _correction;
    private readonly Action _deviceLost;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Rgb[] _buffer;

    private object? _pending;
    private Thread? _thread;
    private long _applied;
    private long _applyTicks;
    private long _failures;
    private int _consecutiveFailures;
    private long _unservedSince;

    // Snapshot taken by the previous TakeStatus call, to turn totals into rates
    private long _lastApplied;
    private long _lastTicks;
    private long _lastStamp = Stopwatch.GetTimestamp();

    public string Name => _device.Name;

    public int LightCount => _device.LightCount;

    /// <param name="mapping">How the five Chroma Link colors are laid out on this device right now.</param>
    /// <param name="deviceLost">Called once when the device stops accepting writes.</param>
    public DeviceWorker(ILightingDevice device, Func<ZoneMapping> mapping, Func<ColorCorrection> correction, Action deviceLost)
    {
        _device = device;
        _mapping = mapping;
        _correction = correction;
        _deviceLost = deviceLost;
        _buffer = new Rgb[device.LightCount];
    }

    public void Start(CancellationToken stoppingToken)
    {
        _thread = new Thread(() => Run(stoppingToken)) { IsBackground = true, Name = $"Apply - {Name}" };
        _thread.Start();
    }

    public void Post(ChromaFrame frame)
    {
        Interlocked.Exchange(ref _pending, frame);
        Interlocked.CompareExchange(ref _unservedSince, Environment.TickCount64, 0);
        _signal.Set();
    }

    /// <summary>
    /// How long the oldest frame not yet written has been waiting, or 0 when the worker is
    /// caught up. It grows without bound when a write never returns: vendor SDKs can block
    /// forever inside a call, which shows no failure and no log line.
    /// </summary>
    public long UnservedMilliseconds
    {
        get
        {
            var since = Interlocked.Read(ref _unservedSince);
            return since == 0 ? 0 : Environment.TickCount64 - since;
        }
    }

    /// <summary>Waits for the worker to finish after its stopping token was cancelled.</summary>
    public void Join()
    {
        _signal.Set();
        _thread?.Join(3000);
    }

    /// <summary>The device's state, with rates measured since the previous call.</summary>
    public DeviceStatus TakeStatus()
    {
        var applied = Interlocked.Read(ref _applied);
        var ticks = Interlocked.Read(ref _applyTicks);
        var now = Stopwatch.GetTimestamp();

        var recent = applied - _lastApplied;
        var seconds = Math.Max((now - _lastStamp) / (double)Stopwatch.Frequency, 0.001);

        var status = new DeviceStatus
        {
            Name = Name,
            Lights = LightCount,
            UpdatesPerSecond = Math.Round(recent / seconds, 1),
            ApplyMilliseconds = recent > 0 ? Math.Round((ticks - _lastTicks) * 1000.0 / Stopwatch.Frequency / recent, 1) : 0,
            Failures = Interlocked.Read(ref _failures)
        };

        _lastApplied = applied;
        _lastTicks = ticks;
        _lastStamp = now;

        return status;
    }

    private void Run(CancellationToken stoppingToken)
    {
        var handles = new[] { _signal, stoppingToken.WaitHandle };
        Span<Rgb> colors = stackalloc Rgb[ChromaFrame.ColorCount];

        while (!stoppingToken.IsCancellationRequested)
        {
            WaitHandle.WaitAny(handles);

            if (stoppingToken.IsCancellationRequested)
                break;

            if (Interlocked.Exchange(ref _pending, null) is not ChromaFrame frame)
                continue;

            try
            {
                var started = Stopwatch.GetTimestamp();
                var correction = _correction();

                for (var i = 0; i < ChromaFrame.ColorCount; i++)
                    colors[i] = correction.Apply(frame[i]);

                ZoneMapper.Fill(_buffer, colors, _mapping());

                _device.Apply(_buffer);

                Interlocked.Add(ref _applyTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref _applied);
                _consecutiveFailures = 0;
            }
            catch (DeviceLostException ex)
            {
                Log.Info($"\"{Name}\": {ex.Message} Reconnecting.");
                _deviceLost();
                return;
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref _failures);

                if (failures == 1 || failures % 500 == 0)
                    Log.Error($"Failed writing to \"{Name}\" ({failures} failure(s) so far)", ex);

                if (++_consecutiveFailures == LostAfterFailures)
                {
                    Log.Error($"\"{Name}\" stopped accepting writes; reconnecting");
                    _deviceLost();
                    return;
                }
            }
            finally
            {
                // The write returned, whatever its outcome; a frame posted meanwhile is
                // picked up at once and the next Post starts the clock again
                Interlocked.Exchange(ref _unservedSince, 0);
            }
        }
    }
}
