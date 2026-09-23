using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// A single logical processor (hardware thread) as addressed by the Windows scheduler.
/// </summary>
/// <remarks>
/// Windows addresses processors as a (group, index-within-group) pair. Affinity masks are always
/// group relative, so <see cref="GroupId"/> must travel with <see cref="GroupRelativeIndex"/>
/// everywhere an affinity decision is made. <see cref="GlobalIndex"/> exists only to give the UI
/// and telemetry a stable flat identifier.
/// </remarks>
public sealed record LogicalProcessor
{
    /// <summary>Processor group this logical processor belongs to.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Zero based index of this processor inside <see cref="GroupId"/> (0-63).</summary>
    public required byte GroupRelativeIndex { get; init; }

    /// <summary>Flat index across all groups, assigned in group then index order.</summary>
    public required int GlobalIndex { get; init; }

    /// <summary>Identifier of the physical core that hosts this logical processor.</summary>
    public required int PhysicalCoreId { get; init; }

    /// <summary>NUMA node the processor belongs to, or <c>0</c> on single node systems.</summary>
    public required uint NumaNodeId { get; init; }

    /// <summary>Raw Windows efficiency class. Larger values indicate higher performance cores.</summary>
    public required byte EfficiencyClass { get; init; }

    /// <summary>Group relative affinity mask that selects only this logical processor.</summary>
    public ulong AffinityMask => 1UL << GroupRelativeIndex;
}

/// <summary>A physical core and the logical processors it hosts.</summary>
public sealed record PhysicalCore
{
    /// <summary>Stable identifier of the core within the machine.</summary>
    public required int CoreId { get; init; }

    /// <summary>Processor group that owns every logical processor of this core.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Global indexes of the logical processors hosted by this core.</summary>
    public required IReadOnlyList<int> LogicalProcessorIndexes { get; init; }

    /// <summary>Group relative affinity mask covering every thread of this core.</summary>
    public required ulong GroupAffinityMask { get; init; }

    /// <summary>Raw Windows efficiency class for the core.</summary>
    public required byte EfficiencyClass { get; init; }

    /// <summary>Functional classification derived from the whole topology.</summary>
    public required CoreClass Class { get; init; }

    /// <summary>NUMA node of the core.</summary>
    public required uint NumaNodeId { get; init; }

    /// <summary><see langword="true"/> when the core exposes more than one hardware thread.</summary>
    public bool IsSimultaneousMultiThreaded => LogicalProcessorIndexes.Count > 1;
}
