using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Hardware;

namespace Velocity.TestSupport;

/// <summary>
/// Builds <see cref="CpuTopology"/> instances that match what Windows reports on real processors.
/// </summary>
/// <remarks>
/// The scheduling logic has to be correct on hardware the team cannot keep in the office: hybrid
/// Intel parts, single and multi-CCD Ryzen parts, machines with more than one processor group. The
/// builder reproduces the shape Windows reports for each of those so the analyzer can be tested
/// against all of them on any build agent.
/// </remarks>
public sealed class CpuTopologyBuilder
{
    private readonly List<LogicalProcessor> _processors = new();
    private readonly List<PhysicalCore> _cores = new();
    private readonly List<CacheDescriptor> _caches = new();
    private readonly List<ProcessorGroup> _groups = new();
    private readonly List<NumaNode> _numaNodes = new();
    private readonly Dictionary<int, IReadOnlyList<int>> _dies = new();

    private CpuVendor _vendor = CpuVendor.Intel;
    private string _brand = "Test processor";

    /// <summary>Sets the vendor and brand string.</summary>
    /// <param name="vendor">Vendor.</param>
    /// <param name="brand">Brand string.</param>
    /// <returns>The builder.</returns>
    public CpuTopologyBuilder WithVendor(CpuVendor vendor, string brand)
    {
        _vendor = vendor;
        _brand = brand;
        return this;
    }

    /// <summary>Adds a block of cores with the same efficiency class.</summary>
    /// <param name="coreCount">Number of physical cores to add.</param>
    /// <param name="threadsPerCore">Hardware threads per core.</param>
    /// <param name="efficiencyClass">Windows efficiency class for the block.</param>
    /// <param name="groupId">Processor group the cores belong to.</param>
    /// <returns>The builder.</returns>
    public CpuTopologyBuilder AddCores(
        int coreCount,
        int threadsPerCore,
        byte efficiencyClass = 0,
        ushort groupId = 0)
    {
        for (int i = 0; i < coreCount; i++)
        {
            int coreId = _cores.Count;
            var indexes = new List<int>();
            ulong mask = 0;

            for (int thread = 0; thread < threadsPerCore; thread++)
            {
                int globalIndex = _processors.Count;
                byte relative = (byte)_processors.Count(processor => processor.GroupId == groupId);

                _processors.Add(new LogicalProcessor
                {
                    GroupId = groupId,
                    GroupRelativeIndex = relative,
                    GlobalIndex = globalIndex,
                    PhysicalCoreId = coreId,
                    NumaNodeId = 0,
                    EfficiencyClass = efficiencyClass,
                });

                indexes.Add(globalIndex);
                mask |= 1UL << relative;
            }

            _cores.Add(new PhysicalCore
            {
                CoreId = coreId,
                GroupId = groupId,
                LogicalProcessorIndexes = indexes,
                GroupAffinityMask = mask,
                EfficiencyClass = efficiencyClass,
                Class = CoreClass.Unknown,
                NumaNodeId = 0,
            });
        }

        return this;
    }

    /// <summary>Adds a cache instance shared by the given cores.</summary>
    /// <param name="level">Cache level.</param>
    /// <param name="sizeBytes">Cache size in bytes.</param>
    /// <param name="coreIds">Cores that share the instance.</param>
    /// <param name="groupId">Processor group the sharing mask belongs to.</param>
    /// <returns>The builder.</returns>
    public CpuTopologyBuilder AddSharedCache(
        byte level,
        uint sizeBytes,
        IEnumerable<int> coreIds,
        ushort groupId = 0)
    {
        var sharedProcessors = new List<int>();
        foreach (int coreId in coreIds)
        {
            sharedProcessors.AddRange(_cores[coreId].LogicalProcessorIndexes);
        }

        _caches.Add(new CacheDescriptor
        {
            Level = level,
            Kind = CacheKind.Unified,
            SizeBytes = sizeBytes,
            LineSizeBytes = 64,
            Associativity = 16,
            GroupId = groupId,
            SharedByLogicalProcessorIndexes = sharedProcessors,
        });

        return this;
    }

    /// <summary>Records a die reported directly by Windows.</summary>
    /// <param name="dieId">Die identifier.</param>
    /// <param name="coreIds">Cores on the die.</param>
    /// <returns>The builder.</returns>
    public CpuTopologyBuilder AddDie(int dieId, IEnumerable<int> coreIds)
    {
        var processors = new List<int>();
        foreach (int coreId in coreIds)
        {
            processors.AddRange(_cores[coreId].LogicalProcessorIndexes);
        }

        _dies[dieId] = processors;
        return this;
    }

    /// <summary>Adds a NUMA node covering the given cores.</summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <param name="coreIds">Cores local to the node.</param>
    /// <param name="groupId">Processor group the node's mask belongs to.</param>
    /// <returns>The builder.</returns>
    public CpuTopologyBuilder AddNumaNode(uint nodeId, IEnumerable<int> coreIds, ushort groupId = 0)
    {
        var processors = new List<int>();
        foreach (int coreId in coreIds)
        {
            processors.AddRange(_cores[coreId].LogicalProcessorIndexes);
        }

        _numaNodes.Add(new NumaNode
        {
            NodeId = nodeId,
            GroupId = groupId,
            LogicalProcessorIndexes = processors,
        });

        return this;
    }

    /// <summary>Produces the topology.</summary>
    /// <returns>The built topology.</returns>
    public CpuTopology Build()
    {
        foreach (IGrouping<ushort, LogicalProcessor> group in _processors.GroupBy(processor => processor.GroupId))
        {
            byte count = (byte)group.Count();
            _groups.Add(new ProcessorGroup
            {
                GroupId = group.Key,
                ActiveProcessorCount = count,
                MaximumProcessorCount = 64,
                ActiveProcessorMask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1,
            });
        }

        if (_numaNodes.Count == 0)
        {
            _numaNodes.Add(new NumaNode
            {
                NodeId = 0,
                GroupId = 0,
                LogicalProcessorIndexes = _processors.Select(processor => processor.GlobalIndex).ToList(),
            });
        }

        List<byte> classes = _cores.Select(core => core.EfficiencyClass).Distinct().OrderBy(value => value).ToList();
        bool hybrid = classes.Count > 1;

        List<PhysicalCore> cores = _cores
            .Select(core => core with
            {
                Class = !hybrid
                    ? CoreClass.Uniform
                    : core.EfficiencyClass == classes[^1]
                        ? CoreClass.Performance
                        : classes.Count >= 3 && core.EfficiencyClass == classes[0]
                            ? CoreClass.LowPowerEfficiency
                            : CoreClass.Efficiency,
            })
            .ToList();

        return new CpuTopology
        {
            Vendor = _vendor,
            BrandString = _brand,
            PhysicalCoreCount = cores.Count,
            LogicalProcessorCount = _processors.Count,
            BaseFrequencyMhz = 3600,
            LogicalProcessors = _processors,
            PhysicalCores = cores,
            Groups = _groups,
            NumaNodes = _numaNodes,
            Caches = _caches,
            Dies = _dies,
            IsHybrid = hybrid,
            IsSimultaneousMultiThreadingEnabled = cores.Exists(core => core.IsSimultaneousMultiThreaded),
        };
    }
}
