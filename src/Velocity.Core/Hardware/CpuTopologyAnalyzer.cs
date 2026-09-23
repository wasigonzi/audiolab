using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Hardware;

namespace Velocity.Core.Hardware;

/// <summary>
/// Turns the raw operating system view of the processor into the layout scheduling decisions are
/// made from.
/// </summary>
/// <remarks>
/// <para>
/// This class is pure: topology in, layout out, no Windows calls. That is what makes it possible
/// to validate the AMD multi-CCD and Intel hybrid reasoning against captured fixtures on a build
/// agent that has neither processor.
/// </para>
/// <para>
/// <b>What is inferred and how.</b> Windows does not expose "this is CCD 1" for AMD parts. The one
/// documented signal it does expose is which logical processors share each cache instance, so core
/// complexes are derived from last level cache sharing: one L3 instance is one complex. On Zen 2
/// and later that boundary coincides with a CCD. Where the platform additionally reports
/// <c>RelationProcessorDie</c>, the reported dies are used to label the complexes instead of
/// inferring the label from cache size.
/// </para>
/// <para>
/// <b>What is not claimed.</b> The preferred core set is a hypothesis for the auto-tune engine to
/// test, not a statement that confining a game to those cores is faster. Nothing here disables SMT
/// or efficiency cores, because neither is reliably a win and neither can be undone without a
/// reboot or firmware change.
/// </para>
/// </remarks>
public static class CpuTopologyAnalyzer
{
    /// <summary>
    /// A preferred core set is only proposed when it leaves at least this many physical cores for
    /// the game. Below that, constraining a game does more harm than the cache locality is worth.
    /// </summary>
    private const int MinimumPreferredCores = 4;

    /// <summary>
    /// Background work is only confined to a separate core set when at least this many physical
    /// cores are left for it, otherwise background threads queue behind each other and the stutter
    /// moves rather than disappearing.
    /// </summary>
    private const int MinimumBackgroundCores = 2;

    /// <summary>Analyzes a topology.</summary>
    /// <param name="topology">Raw topology from the operating system.</param>
    /// <returns>The interpreted layout.</returns>
    public static CpuLayout Analyze(CpuTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var notes = new List<string>();
        IReadOnlyList<PhysicalCore> cores = topology.PhysicalCores;

        if (cores.Count == 0)
        {
            return new CpuLayout
            {
                Topology = topology,
                PerformanceCoreIds = Array.Empty<int>(),
                EfficiencyCoreIds = Array.Empty<int>(),
                Complexes = Array.Empty<CoreComplex>(),
                PreferredGameCoreIds = Array.Empty<int>(),
                BackgroundCoreIds = Array.Empty<int>(),
                Notes = new[] { "The operating system reported no physical cores; CPU modules are unavailable." },
            };
        }

        (List<int> performanceCores, List<int> efficiencyCores) = ClassifyCores(topology, notes);
        List<CoreComplex> complexes = DetectComplexes(topology, notes);
        List<int> preferred = ChoosePreferredCores(topology, performanceCores, complexes, notes);
        List<int> background = ChooseBackgroundCores(cores, preferred, notes);

        AddStructuralNotes(topology, notes);

        return new CpuLayout
        {
            Topology = topology,
            PerformanceCoreIds = performanceCores,
            EfficiencyCoreIds = efficiencyCores,
            Complexes = complexes,
            PreferredGameCoreIds = preferred,
            BackgroundCoreIds = background,
            Notes = notes,
        };
    }

    private static (List<int> Performance, List<int> Efficiency) ClassifyCores(
        CpuTopology topology,
        List<string> notes)
    {
        List<byte> classes = topology.PhysicalCores
            .Select(core => core.EfficiencyClass)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        var performance = new List<int>();
        var efficiency = new List<int>();

        if (classes.Count <= 1)
        {
            // Homogeneous: every core is a performance core by definition, and calling the single
            // efficiency class "0" an efficiency core would be a reporting bug, not a discovery.
            performance.AddRange(topology.PhysicalCores.Select(core => core.CoreId));
            return (performance, efficiency);
        }

        byte highest = classes[^1];
        foreach (PhysicalCore core in topology.PhysicalCores)
        {
            if (core.EfficiencyClass == highest)
            {
                performance.Add(core.CoreId);
            }
            else
            {
                efficiency.Add(core.CoreId);
            }
        }

        notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Hybrid processor detected: {performance.Count} performance core(s) and {efficiency.Count} efficiency core(s) across {classes.Count} efficiency classes."));

        return (performance, efficiency);
    }

    private static List<CoreComplex> DetectComplexes(CpuTopology topology, List<string> notes)
    {
        var complexes = new List<CoreComplex>();
        Dictionary<int, PhysicalCore> coreByLogicalProcessor = BuildCoreLookup(topology);

        // Primary signal: last level cache sharing. One L3 instance is one complex; on parts with
        // no L3 at all, fall back to L2.
        byte lastLevel = topology.Caches.Count == 0
            ? (byte)0
            : topology.Caches.Max(cache => cache.Level);

        if (lastLevel >= 2)
        {
            IEnumerable<CacheDescriptor> lastLevelCaches = topology.Caches
                .Where(cache => cache.Level == lastLevel && cache.Kind != CacheKind.Instruction);

            int complexId = 0;
            foreach (CacheDescriptor cache in lastLevelCaches.OrderBy(c => c.SharedByLogicalProcessorIndexes.Count == 0
                ? int.MaxValue
                : c.SharedByLogicalProcessorIndexes.Min()))
            {
                List<int> coreIds = ResolveCoreIds(cache, coreByLogicalProcessor);
                if (coreIds.Count == 0)
                {
                    continue;
                }

                complexes.Add(new CoreComplex
                {
                    ComplexId = complexId++,
                    Kind = ResolveComplexKind(topology, cache, coreIds),
                    PhysicalCoreIds = coreIds,
                    SharedCacheBytes = cache.SizeBytes,
                    SharedCacheLevel = cache.Level,
                });
            }
        }

        if (complexes.Count > 1)
        {
            IEnumerable<string> descriptions = complexes.Select(complex => string.Create(
                CultureInfo.InvariantCulture,
                $"#{complex.ComplexId}: {complex.PhysicalCoreIds.Count} cores, {complex.SharedCacheBytes / (1024 * 1024)} MB L{complex.SharedCacheLevel}"));

            notes.Add(
                $"{complexes.Count} core complexes share separate last level caches ({string.Join("; ", descriptions)}). " +
                "Threads that migrate between complexes pay a cross-cache penalty.");
        }

        return complexes;
    }

    private static CoreComplexKind ResolveComplexKind(
        CpuTopology topology,
        CacheDescriptor cache,
        List<int> coreIds)
    {
        if (topology.Dies.Count > 1)
        {
            // Windows reported die relationships; trust them over an inference from cache size.
            foreach (KeyValuePair<int, IReadOnlyList<int>> die in topology.Dies)
            {
                if (die.Value.Count > 0 &&
                    cache.SharedByLogicalProcessorIndexes.All(die.Value.Contains))
                {
                    return CoreComplexKind.ProcessorDie;
                }
            }
        }

        if (cache.Level <= 2)
        {
            // An L2 shared by several cores is a cluster, which on current Intel hybrid parts is a
            // group of efficiency cores.
            return coreIds.Count > 1 ? CoreComplexKind.EfficiencyCluster : CoreComplexKind.Unknown;
        }

        return topology.Vendor == CpuVendor.Amd
            ? CoreComplexKind.CoreComplex
            : CoreComplexKind.ProcessorDie;
    }

    private static List<int> ChoosePreferredCores(
        CpuTopology topology,
        List<int> performanceCores,
        List<CoreComplex> complexes,
        List<string> notes)
    {
        IReadOnlyList<PhysicalCore> cores = topology.PhysicalCores;
        var allCoreIds = cores.Select(core => core.CoreId).ToList();

        // Hybrid parts: the performance cores are the candidate set, provided there are enough.
        if (performanceCores.Count > 0 && performanceCores.Count < cores.Count)
        {
            if (performanceCores.Count >= MinimumPreferredCores)
            {
                notes.Add(
                    "Candidate game core set: the performance cores. Whether confining a game to them " +
                    "helps is decided by measurement, not assumed.");
                return performanceCores;
            }

            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Only {performanceCores.Count} performance core(s) are present, which is too few to confine a game to. All cores remain available."));
            return allCoreIds;
        }

        // Homogeneous parts with more than one last level cache: the best complex is the candidate.
        List<CoreComplex> usableComplexes = complexes
            .Where(complex => complex.SharedCacheLevel >= 3 && complex.PhysicalCoreIds.Count > 0)
            .ToList();

        if (usableComplexes.Count > 1)
        {
            CoreComplex best = usableComplexes
                .OrderByDescending(complex => complex.SharedCacheBytes)
                .ThenByDescending(complex => complex.PhysicalCoreIds.Count)
                .ThenBy(complex => complex.ComplexId)
                .First();

            bool cacheSizesDiffer = usableComplexes
                .Select(complex => complex.SharedCacheBytes)
                .Distinct()
                .Count() > 1;

            if (best.PhysicalCoreIds.Count >= MinimumPreferredCores)
            {
                notes.Add(cacheSizesDiffer
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"Complex #{best.ComplexId} has the largest last level cache ({best.SharedCacheBytes / (1024 * 1024)} MB) and is the candidate game core set.")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"All complexes have equal cache; complex #{best.ComplexId} is the candidate game core set purely to keep threads on one complex."));

                return best.PhysicalCoreIds.ToList();
            }

            notes.Add(
                "No single core complex is large enough to host a game on its own; all cores remain available.");
        }

        return allCoreIds;
    }

    private static List<int> ChooseBackgroundCores(
        IReadOnlyList<PhysicalCore> cores,
        List<int> preferred,
        List<string> notes)
    {
        var preferredSet = preferred.ToHashSet();
        List<int> remaining = cores
            .Select(core => core.CoreId)
            .Where(id => !preferredSet.Contains(id))
            .ToList();

        if (remaining.Count < MinimumBackgroundCores)
        {
            if (remaining.Count > 0)
            {
                notes.Add(
                    "Too few cores are left outside the candidate game set to isolate background work; " +
                    "background processes will be de-prioritised rather than confined.");
            }

            return new List<int>();
        }

        return remaining;
    }

    private static void AddStructuralNotes(CpuTopology topology, List<string> notes)
    {
        if (topology.Groups.Count > 1)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"This machine has {topology.Groups.Count} processor groups. Affinity masks are group relative, so any core selection must stay inside one group."));
        }

        if (topology.NumaNodes.Count > 1)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{topology.NumaNodes.Count} NUMA nodes are present. Memory locality dominates core selection on this class of machine."));
        }

        if (topology.IsSimultaneousMultiThreadingEnabled)
        {
            notes.Add(
                "Simultaneous multithreading is enabled. It is left enabled: disabling it is a firmware " +
                "level change, is not reversible from Windows, and is not reliably faster for games.");
        }
    }

    private static Dictionary<int, PhysicalCore> BuildCoreLookup(CpuTopology topology)
    {
        var lookup = new Dictionary<int, PhysicalCore>();
        foreach (PhysicalCore core in topology.PhysicalCores)
        {
            foreach (int logicalProcessor in core.LogicalProcessorIndexes)
            {
                lookup[logicalProcessor] = core;
            }
        }

        return lookup;
    }

    private static List<int> ResolveCoreIds(
        CacheDescriptor cache,
        Dictionary<int, PhysicalCore> coreByLogicalProcessor)
    {
        var coreIds = new List<int>();
        foreach (int logicalProcessor in cache.SharedByLogicalProcessorIndexes)
        {
            if (coreByLogicalProcessor.TryGetValue(logicalProcessor, out PhysicalCore? core) &&
                !coreIds.Contains(core.CoreId))
            {
                coreIds.Add(core.CoreId);
            }
        }

        coreIds.Sort();
        return coreIds;
    }
}
