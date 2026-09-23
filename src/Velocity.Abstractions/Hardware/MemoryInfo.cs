using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>A physical memory module.</summary>
public sealed record MemoryModule
{
    /// <summary>Slot label reported by SMBIOS, for example <c>DIMM 0</c>.</summary>
    public required string Slot { get; init; }

    /// <summary>Module capacity in bytes.</summary>
    public required long CapacityBytes { get; init; }

    /// <summary>Configured speed in MT/s, or <c>0</c> when unknown.</summary>
    public int ConfiguredSpeedMtps { get; init; }

    /// <summary>Module manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Module part number.</summary>
    public string? PartNumber { get; init; }
}

/// <summary>
/// Memory state of the machine.
/// </summary>
/// <remarks>
/// Every field here is an observation. The memory module deliberately exposes commit and fault
/// telemetry rather than a "free RAM" headline, because emptying working sets or the standby list
/// to make a number look better is exactly the placebo behaviour this product does not ship.
/// </remarks>
public sealed record MemoryInfo
{
    /// <summary>Total installed physical memory in bytes.</summary>
    public required long TotalPhysicalBytes { get; init; }

    /// <summary>Physical memory currently available in bytes.</summary>
    public required long AvailablePhysicalBytes { get; init; }

    /// <summary>Current system commit charge in bytes.</summary>
    public long CommitTotalBytes { get; init; }

    /// <summary>Current system commit limit in bytes.</summary>
    public long CommitLimitBytes { get; init; }

    /// <summary>Peak commit charge observed since boot, in bytes.</summary>
    public long CommitPeakBytes { get; init; }

    /// <summary>System page size in bytes.</summary>
    public int PageSizeBytes { get; init; }

    /// <summary>Installed memory modules, when SMBIOS data is readable.</summary>
    public IReadOnlyList<MemoryModule> Modules { get; init; } = new List<MemoryModule>();

    /// <summary>Whether the page file is managed by the system, if determinable.</summary>
    public bool? SystemManagedPageFile { get; init; }

    /// <summary>Total page file size across all volumes in bytes, when readable.</summary>
    public long? PageFileSizeBytes { get; init; }

    /// <summary>Fraction of installed memory currently in use, from 0 to 1.</summary>
    public double UtilizationRatio => TotalPhysicalBytes <= 0
        ? 0d
        : 1d - ((double)AvailablePhysicalBytes / TotalPhysicalBytes);
}
