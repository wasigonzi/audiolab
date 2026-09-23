using System;
using System.Threading;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Sessions;

/// <summary>
/// Averages the telemetry samples produced while a game runs.
/// </summary>
/// <remarks>
/// <para>
/// Each counter is averaged over the samples that actually carried it, and a counter no sample
/// carried comes back as zero with a count of zero behind it rather than as a fabricated figure.
/// That is why the summary is only produced when at least one sample arrived.
/// </para>
/// <para>
/// The accumulator runs on the monitor's sampling thread, so it does the least work it can: a few
/// interlocked-free additions under a small lock. Anything heavier here would make the optimizer a
/// source of the stutter it exists to remove.
/// </para>
/// </remarks>
public sealed class TelemetryAccumulator
{
    private readonly Lock _gate = new();

    private double _cpuTotal;
    private int _cpuCount;
    private double _backgroundCpuTotal;
    private int _backgroundCpuCount;
    private double _gpuTotal;
    private int _gpuCount;
    private double _committedTotal;
    private int _committedCount;
    private double _diskTotal;
    private int _diskCount;
    private int _sampleCount;

    /// <summary>Number of samples accumulated.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _sampleCount;
            }
        }
    }

    /// <summary>Subscribes to a monitor for as long as the returned handle is held.</summary>
    /// <param name="monitor">Monitor to subscribe to, or <see langword="null"/> for no telemetry.</param>
    /// <returns>A handle that unsubscribes when disposed.</returns>
    public IDisposable Attach(ISystemMonitor? monitor)
    {
        if (monitor is null)
        {
            return new Subscription(null, null);
        }

        void Handler(object? sender, TelemetrySample sample) => Add(sample);

        monitor.SampleProduced += Handler;
        return new Subscription(monitor, Handler);
    }

    /// <summary>Adds one sample.</summary>
    /// <param name="sample">Sample to add.</param>
    public void Add(TelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_gate)
        {
            _sampleCount++;

            Accumulate(sample.CpuUtilization, ref _cpuTotal, ref _cpuCount);
            Accumulate(sample.BackgroundCpuUtilization, ref _backgroundCpuTotal, ref _backgroundCpuCount);
            Accumulate(sample.GpuUtilization, ref _gpuTotal, ref _gpuCount);
            Accumulate(sample.CommittedBytes, ref _committedTotal, ref _committedCount);
            Accumulate(sample.DiskBytesPerSecond, ref _diskTotal, ref _diskCount);
        }
    }

    /// <summary>Produces the averages, or null when no sample arrived.</summary>
    /// <returns>The statistics, or <see langword="null"/>.</returns>
    public ResourceStatistics? Summarize()
    {
        lock (_gate)
        {
            if (_sampleCount == 0)
            {
                return null;
            }

            return new ResourceStatistics
            {
                MeanCpuUtilization = Mean(_cpuTotal, _cpuCount),
                MeanBackgroundCpuUtilization = Mean(_backgroundCpuTotal, _backgroundCpuCount),
                MeanGpuUtilization = Mean(_gpuTotal, _gpuCount),
                MeanCommittedBytes = (long)Mean(_committedTotal, _committedCount),
                MeanDiskBytesPerSecond = Mean(_diskTotal, _diskCount),
            };
        }
    }

    private static void Accumulate(double? value, ref double total, ref int count)
    {
        if (value is not null && !double.IsNaN(value.Value))
        {
            total += value.Value;
            count++;
        }
    }

    private static void Accumulate(long? value, ref double total, ref int count)
    {
        if (value is not null)
        {
            total += value.Value;
            count++;
        }
    }

    private static double Mean(double total, int count) => count == 0 ? 0d : total / count;

    private sealed class Subscription : IDisposable
    {
        private readonly ISystemMonitor? _monitor;
        private readonly EventHandler<TelemetrySample>? _handler;

        internal Subscription(ISystemMonitor? monitor, EventHandler<TelemetrySample>? handler)
        {
            _monitor = monitor;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_monitor is not null && _handler is not null)
            {
                _monitor.SampleProduced -= _handler;
            }
        }
    }
}
