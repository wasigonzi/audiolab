using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// The interpreted view of <see cref="CpuTopology"/> that scheduling decisions are made from.
/// </summary>
/// <remarks>
/// Produced by the topology analyzer. Nothing in here is measured; it is a structural
/// interpretation only. Whether placing a game on <see cref="PreferredGameCoreIds"/> actually
/// helps is decided by benchmarking, never by this record.
/// </remarks>
public sealed record CpuLayout
{
    /// <summary>Topology this layout was derived from.</summary>
    public required CpuTopology Topology { get; init; }

    /// <summary>Core ids classified as performance cores, or all cores on a homogeneous part.</summary>
    public required IReadOnlyList<int> PerformanceCoreIds { get; init; }

    /// <summary>Core ids classified as efficiency cores. Empty on homogeneous parts.</summary>
    public required IReadOnlyList<int> EfficiencyCoreIds { get; init; }

    /// <summary>Detected packaging complexes (AMD CCX/CCD, Intel E-core clusters, reported dies).</summary>
    public required IReadOnlyList<CoreComplex> Complexes { get; init; }

    /// <summary>
    /// Cores that the analyzer would try first for a latency sensitive foreground workload.
    /// </summary>
    /// <remarks>
    /// This is a starting hypothesis for the auto-tune engine, not a claim of improvement. On a
    /// multi-CCD Ryzen part it is the cores of the single best complex (largest last level cache,
    /// tie broken by lowest complex id); on an Intel hybrid part it is the performance cores; on a
    /// homogeneous part it is every core, which means "do not constrain the game".
    /// </remarks>
    public required IReadOnlyList<int> PreferredGameCoreIds { get; init; }

    /// <summary>
    /// Cores the analyzer considers acceptable for background work when the game is confined to
    /// <see cref="PreferredGameCoreIds"/>. Empty when confining background work is not possible
    /// without starving it (for example on a 4 core machine).
    /// </summary>
    public required IReadOnlyList<int> BackgroundCoreIds { get; init; }

    /// <summary>Structural observations worth surfacing to the user, in plain language.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary><see langword="true"/> when the machine spans more than one processor group.</summary>
    public bool SpansMultipleProcessorGroups => Topology.Groups.Count > 1;
}
