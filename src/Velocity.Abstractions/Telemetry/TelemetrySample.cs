using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Telemetry;

/// <summary>
/// One instant of live system telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Every section is nullable because a counter that a platform does not expose must read as
/// "unknown" rather than as zero. A dashboard showing 0% GPU utilization because the counter is
/// missing is worse than one showing a dash.
/// </para>
/// <para>
/// Samples are immutable so the monitor can hand the same instance to several observers without
/// copying, which matters when the sampling loop must stay cheap during a game.
/// </para>
/// </remarks>
public sealed record TelemetrySample
{
    /// <summary>When the sample was taken.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Total processor utilization from 0 to 1.</summary>
    public double? CpuUtilization { get; init; }

    /// <summary>Per logical processor utilization from 0 to 1, indexed by global processor index.</summary>
    public IReadOnlyList<double>? PerCoreUtilization { get; init; }

    /// <summary>Current processor frequency in MHz, when the platform reports it.</summary>
    public double? CpuFrequencyMhz { get; init; }

    /// <summary>
    /// Processor utilization attributable to processes other than the foreground game.
    /// This is the number the Windows background module exists to reduce.
    /// </summary>
    public double? BackgroundCpuUtilization { get; init; }

    /// <summary>Graphics utilization from 0 to 1, summed across engines.</summary>
    public double? GpuUtilization { get; init; }

    /// <summary>Dedicated video memory in use, in bytes.</summary>
    public long? VideoMemoryUsedBytes { get; init; }

    /// <summary>Physical memory in use, in bytes.</summary>
    public long? MemoryUsedBytes { get; init; }

    /// <summary>Total physical memory, in bytes.</summary>
    public long? MemoryTotalBytes { get; init; }

    /// <summary>Committed memory, in bytes.</summary>
    public long? CommittedBytes { get; init; }

    /// <summary>Disk transfer rate across all physical disks, in bytes per second.</summary>
    public double? DiskBytesPerSecond { get; init; }

    /// <summary>Current disk queue length across all physical disks.</summary>
    public double? DiskQueueLength { get; init; }

    /// <summary>Network throughput across active adapters, in bytes per second.</summary>
    public double? NetworkBytesPerSecond { get; init; }

    /// <summary>Executable name of the detected foreground game, when one is running.</summary>
    public string? ActiveGameExecutable { get; init; }

    /// <summary>Fraction of installed memory in use, from 0 to 1, when both figures are known.</summary>
    public double? MemoryUtilization =>
        MemoryTotalBytes is > 0 && MemoryUsedBytes is not null
            ? (double)MemoryUsedBytes.Value / MemoryTotalBytes.Value
            : null;
}

/// <summary>Produces one section of a telemetry sample.</summary>
/// <remarks>
/// Providers are composed rather than inherited so that a platform can supply some counters and not
/// others, and so a failing provider can be dropped without taking the dashboard down.
/// </remarks>
public interface ITelemetryProvider : IDisposable
{
    /// <summary>Stable name, used in failure reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Contributes this provider's fields to the sample being assembled.
    /// </summary>
    /// <param name="builder">Accumulator for the sample under construction.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>A task that completes when the provider's fields have been written.</returns>
    System.Threading.Tasks.Task ContributeAsync(
        TelemetrySampleBuilder builder,
        System.Threading.CancellationToken cancellationToken);
}

/// <summary>Mutable accumulator used while a <see cref="TelemetrySample"/> is being assembled.</summary>
public sealed class TelemetrySampleBuilder
{
    /// <summary>Total processor utilization from 0 to 1.</summary>
    public double? CpuUtilization { get; set; }

    /// <summary>Per logical processor utilization from 0 to 1.</summary>
    public IReadOnlyList<double>? PerCoreUtilization { get; set; }

    /// <summary>Current processor frequency in MHz.</summary>
    public double? CpuFrequencyMhz { get; set; }

    /// <summary>Processor utilization outside the foreground game.</summary>
    public double? BackgroundCpuUtilization { get; set; }

    /// <summary>Graphics utilization from 0 to 1.</summary>
    public double? GpuUtilization { get; set; }

    /// <summary>Dedicated video memory in use, in bytes.</summary>
    public long? VideoMemoryUsedBytes { get; set; }

    /// <summary>Physical memory in use, in bytes.</summary>
    public long? MemoryUsedBytes { get; set; }

    /// <summary>Total physical memory, in bytes.</summary>
    public long? MemoryTotalBytes { get; set; }

    /// <summary>Committed memory, in bytes.</summary>
    public long? CommittedBytes { get; set; }

    /// <summary>Disk transfer rate in bytes per second.</summary>
    public double? DiskBytesPerSecond { get; set; }

    /// <summary>Disk queue length.</summary>
    public double? DiskQueueLength { get; set; }

    /// <summary>Network throughput in bytes per second.</summary>
    public double? NetworkBytesPerSecond { get; set; }

    /// <summary>Executable name of the detected foreground game.</summary>
    public string? ActiveGameExecutable { get; set; }

    /// <summary>Produces the immutable sample.</summary>
    /// <param name="timestampUtc">Timestamp to stamp the sample with.</param>
    /// <returns>The sample.</returns>
    public TelemetrySample Build(DateTimeOffset timestampUtc) => new()
    {
        TimestampUtc = timestampUtc,
        CpuUtilization = CpuUtilization,
        PerCoreUtilization = PerCoreUtilization,
        CpuFrequencyMhz = CpuFrequencyMhz,
        BackgroundCpuUtilization = BackgroundCpuUtilization,
        GpuUtilization = GpuUtilization,
        VideoMemoryUsedBytes = VideoMemoryUsedBytes,
        MemoryUsedBytes = MemoryUsedBytes,
        MemoryTotalBytes = MemoryTotalBytes,
        CommittedBytes = CommittedBytes,
        DiskBytesPerSecond = DiskBytesPerSecond,
        DiskQueueLength = DiskQueueLength,
        NetworkBytesPerSecond = NetworkBytesPerSecond,
        ActiveGameExecutable = ActiveGameExecutable,
    };
}
