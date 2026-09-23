using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>A Windows processor group. Each group holds at most 64 logical processors.</summary>
public sealed record ProcessorGroup
{
    /// <summary>Zero based group number.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Number of logical processors currently active in the group.</summary>
    public required byte ActiveProcessorCount { get; init; }

    /// <summary>Maximum number of logical processors the group can hold.</summary>
    public required byte MaximumProcessorCount { get; init; }

    /// <summary>Affinity mask of the currently active processors in the group.</summary>
    public required ulong ActiveProcessorMask { get; init; }
}

/// <summary>A NUMA node and the processors attached to it.</summary>
public sealed record NumaNode
{
    /// <summary>Node number as reported by Windows.</summary>
    public required uint NodeId { get; init; }

    /// <summary>Processor group the node's affinity mask belongs to.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Global indexes of logical processors local to this node.</summary>
    public required IReadOnlyList<int> LogicalProcessorIndexes { get; init; }
}

/// <summary>
/// A set of cores that share a packaging boundary (AMD CCX/CCD, Intel E-core cluster, or a die
/// reported directly by Windows).
/// </summary>
public sealed record CoreComplex
{
    /// <summary>Identifier assigned by the topology analyzer, stable for a given topology.</summary>
    public required int ComplexId { get; init; }

    /// <summary>What kind of packaging boundary this complex represents.</summary>
    public required CoreComplexKind Kind { get; init; }

    /// <summary>Identifiers of the physical cores in the complex.</summary>
    public required IReadOnlyList<int> PhysicalCoreIds { get; init; }

    /// <summary>Size of the cache instance that defined the boundary, in bytes.</summary>
    public required uint SharedCacheBytes { get; init; }

    /// <summary>Level of the cache instance that defined the boundary.</summary>
    public required byte SharedCacheLevel { get; init; }
}
