using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Telemetry;

/// <summary>
/// Frame pacing statistics derived from a series of frame times.
/// </summary>
/// <remarks>
/// Frame time, not frame rate, is the primary measurement. Averaging frames per second hides
/// exactly the stutter this product exists to remove, so the aggregates are computed on the frame
/// time series and frame rate figures are derived from them for display.
/// </remarks>
public sealed record FrameTimeStatistics
{
    /// <summary>Number of frames in the sample.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Wall clock duration covered by the sample.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Mean frame time in milliseconds.</summary>
    public required double MeanFrameTimeMs { get; init; }

    /// <summary>Median (P50) frame time in milliseconds.</summary>
    public required double MedianFrameTimeMs { get; init; }

    /// <summary>95th percentile frame time in milliseconds.</summary>
    public required double P95FrameTimeMs { get; init; }

    /// <summary>99th percentile frame time in milliseconds.</summary>
    public required double P99FrameTimeMs { get; init; }

    /// <summary>99.9th percentile frame time in milliseconds.</summary>
    public required double P999FrameTimeMs { get; init; }

    /// <summary>Standard deviation of frame times in milliseconds.</summary>
    public required double StandardDeviationMs { get; init; }

    /// <summary>Average frames per second over the sample.</summary>
    public double AverageFps => MeanFrameTimeMs > 0 ? 1000d / MeanFrameTimeMs : 0d;

    /// <summary>
    /// The "1% low" figure, expressed as the frame rate equivalent of the 99th percentile frame time.
    /// </summary>
    public double OnePercentLowFps => P99FrameTimeMs > 0 ? 1000d / P99FrameTimeMs : 0d;

    /// <summary>
    /// The "0.1% low" figure, expressed as the frame rate equivalent of the 99.9th percentile
    /// frame time.
    /// </summary>
    public double PointOnePercentLowFps => P999FrameTimeMs > 0 ? 1000d / P999FrameTimeMs : 0d;
}

/// <summary>System resource statistics recorded alongside a frame time sample.</summary>
public sealed record ResourceStatistics
{
    /// <summary>Mean total CPU utilization as a fraction from 0 to 1.</summary>
    public double MeanCpuUtilization { get; init; }

    /// <summary>
    /// Mean CPU utilization attributable to processes other than the measured game, from 0 to 1.
    /// This is the number the Windows background module exists to reduce.
    /// </summary>
    public double MeanBackgroundCpuUtilization { get; init; }

    /// <summary>Mean GPU utilization as a fraction from 0 to 1.</summary>
    public double MeanGpuUtilization { get; init; }

    /// <summary>Mean committed memory in bytes.</summary>
    public long MeanCommittedBytes { get; init; }

    /// <summary>Mean disk transfer rate in bytes per second across all devices.</summary>
    public double MeanDiskBytesPerSecond { get; init; }

    /// <summary>Mean round trip time to the measurement endpoint in milliseconds, when measured.</summary>
    public double? MeanNetworkLatencyMs { get; init; }

    /// <summary>Round trip time jitter in milliseconds, when measured.</summary>
    public double? NetworkJitterMs { get; init; }
}

/// <summary>One complete benchmark measurement.</summary>
public sealed record BenchmarkRun
{
    /// <summary>Run identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Fingerprint of the machine the run was measured on.</summary>
    public required string HardwareFingerprint { get; init; }

    /// <summary>Game or workload the run measured.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>Transaction whose state was in effect during the run, when there was one.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>Label distinguishing baseline runs from candidate runs.</summary>
    public required string Label { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Frame pacing statistics, when frame data was available.</summary>
    public FrameTimeStatistics? FrameTimes { get; init; }

    /// <summary>Resource statistics.</summary>
    public ResourceStatistics Resources { get; init; } = new();

    /// <summary>Tweak identifiers that were applied during the run.</summary>
    public IReadOnlyList<string> AppliedTweakIds { get; init; } = Array.Empty<string>();
}
