using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// The raw processor topology as reported by the operating system.
/// </summary>
/// <remarks>
/// This record is intentionally a faithful transcription of what Windows reports through
/// <c>GetLogicalProcessorInformationEx</c> and the processor registry keys. All interpretation
/// (hybrid classification, CCD inference, which cores a game should run on) happens in the
/// topology analyzer so that the interpretation can be unit tested against captured fixtures
/// from machines the development team does not physically own.
/// </remarks>
public sealed record CpuTopology
{
    /// <summary>Detected processor vendor.</summary>
    public required CpuVendor Vendor { get; init; }

    /// <summary>Marketing brand string, for example <c>AMD Ryzen 9 7950X3D 16-Core Processor</c>.</summary>
    public required string BrandString { get; init; }

    /// <summary>Processor identifier string as recorded by Windows, if available.</summary>
    public string? ProcessorIdentifier { get; init; }

    /// <summary>Number of physical cores across all groups.</summary>
    public required int PhysicalCoreCount { get; init; }

    /// <summary>Number of logical processors across all groups.</summary>
    public required int LogicalProcessorCount { get; init; }

    /// <summary>Nominal base frequency in MHz as reported by the firmware, or <c>0</c> if unknown.</summary>
    public int BaseFrequencyMhz { get; init; }

    /// <summary>All logical processors, ordered by <see cref="LogicalProcessor.GlobalIndex"/>.</summary>
    public required IReadOnlyList<LogicalProcessor> LogicalProcessors { get; init; }

    /// <summary>All physical cores, ordered by <see cref="PhysicalCore.CoreId"/>.</summary>
    public required IReadOnlyList<PhysicalCore> PhysicalCores { get; init; }

    /// <summary>All processor groups.</summary>
    public required IReadOnlyList<ProcessorGroup> Groups { get; init; }

    /// <summary>All NUMA nodes.</summary>
    public required IReadOnlyList<NumaNode> NumaNodes { get; init; }

    /// <summary>All cache instances reported by the operating system.</summary>
    public required IReadOnlyList<CacheDescriptor> Caches { get; init; }

    /// <summary>
    /// Die identifiers reported directly by Windows through <c>RelationProcessorDie</c>, keyed by
    /// die number with the global logical processor indexes as values. Empty when the platform
    /// does not report die relationships.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> Dies { get; init; }
        = new Dictionary<int, IReadOnlyList<int>>();

    /// <summary><see langword="true"/> when cores report more than one distinct efficiency class.</summary>
    public bool IsHybrid { get; init; }

    /// <summary><see langword="true"/> when at least one core exposes multiple hardware threads.</summary>
    public bool IsSimultaneousMultiThreadingEnabled { get; init; }
}
