namespace Velocity.Abstractions.Hardware;

/// <summary>Identifies the vendor of the installed processor.</summary>
public enum CpuVendor
{
    /// <summary>The vendor could not be determined.</summary>
    Unknown = 0,

    /// <summary>Intel.</summary>
    Intel = 1,

    /// <summary>AMD.</summary>
    Amd = 2,

    /// <summary>An ARM implementation (Qualcomm, Ampere, Apple, ...).</summary>
    Arm = 3,

    /// <summary>A vendor recognised by name but not specifically supported.</summary>
    Other = 99,
}

/// <summary>
/// Functional classification of a physical core, derived from the Windows scheduler's
/// efficiency class plus vendor specific topology heuristics.
/// </summary>
/// <remarks>
/// Windows exposes <c>EfficiencyClass</c> on <c>PROCESSOR_RELATIONSHIP</c>. Higher values are
/// more performant. On homogeneous processors every core reports class 0, which is why a value
/// of zero alone never implies "efficiency core".
/// </remarks>
public enum CoreClass
{
    /// <summary>Classification is not available.</summary>
    Unknown = 0,

    /// <summary>The processor is homogeneous; all cores are equivalent.</summary>
    Uniform = 1,

    /// <summary>A performance ("P") core on a hybrid processor.</summary>
    Performance = 2,

    /// <summary>An efficiency ("E") core on a hybrid processor.</summary>
    Efficiency = 3,

    /// <summary>A low power efficiency core (for example Intel LP E-cores on the SoC tile).</summary>
    LowPowerEfficiency = 4,
}

/// <summary>Describes how a group of cores is physically packaged.</summary>
public enum CoreComplexKind
{
    /// <summary>The grouping could not be attributed to a known packaging concept.</summary>
    Unknown = 0,

    /// <summary>A group of cores sharing one last level cache slice (AMD CCX).</summary>
    CoreComplex = 1,

    /// <summary>An AMD core chiplet die, inferred from last level cache sharing.</summary>
    CoreChipletDie = 2,

    /// <summary>A die reported directly by Windows through <c>RelationProcessorDie</c>.</summary>
    ProcessorDie = 3,

    /// <summary>A module/cluster of efficiency cores sharing an L2 cache (Intel hybrid).</summary>
    EfficiencyCluster = 4,
}

/// <summary>The kind of processor cache described by a <see cref="CacheDescriptor"/>.</summary>
public enum CacheKind
{
    /// <summary>Unified instruction and data cache.</summary>
    Unified = 0,

    /// <summary>Instruction cache.</summary>
    Instruction = 1,

    /// <summary>Data cache.</summary>
    Data = 2,

    /// <summary>Trace cache.</summary>
    Trace = 3,
}
