using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Hardware;
using Velocity.Core.Hardware;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// The analyzer decides which cores a game would be confined to. These tests pin that behaviour to
/// the topologies of real processors, because a mistake here is not a cosmetic bug: it would move
/// a game onto the wrong chiplet or onto efficiency cores.
/// </summary>
public sealed class CpuTopologyAnalyzerTests
{
    [Fact]
    public void HomogeneousDesktop_TreatsEveryCoreAsPerformance()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.IntelDesktopEightCore());

        Assert.Equal(8, layout.PerformanceCoreIds.Count);
        Assert.Empty(layout.EfficiencyCoreIds);
    }

    [Fact]
    public void HomogeneousDesktop_DoesNotConstrainTheGame()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.IntelDesktopEightCore());

        // One last level cache means there is no locality to gain, so every core stays available.
        Assert.Equal(8, layout.PreferredGameCoreIds.Count);
        Assert.Empty(layout.BackgroundCoreIds);
    }

    [Fact]
    public void HybridProcessor_SeparatesPerformanceFromEfficiencyCores()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.IntelHybridPerformanceAndEfficiency());

        Assert.Equal(8, layout.PerformanceCoreIds.Count);
        Assert.Equal(16, layout.EfficiencyCoreIds.Count);
        Assert.Equal(layout.PerformanceCoreIds, layout.PreferredGameCoreIds);
        Assert.Equal(16, layout.BackgroundCoreIds.Count);
    }

    [Fact]
    public void HybridProcessor_DetectsEfficiencyCoreClusters()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.IntelHybridPerformanceAndEfficiency());

        // The single L3 is the last level, so complexes come from it; the L2 clusters are recorded
        // in the topology but do not split the last level cache grouping.
        Assert.Single(layout.Complexes);
        Assert.Equal(3, layout.Complexes[0].SharedCacheLevel);
    }

    [Fact]
    public void SingleChipletRyzen_LeavesEveryCoreAvailable()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.AmdSingleChiplet());

        Assert.Single(layout.Complexes);
        Assert.Equal(8, layout.PreferredGameCoreIds.Count);
    }

    [Fact]
    public void DualChipletRyzen_PrefersTheChipletWithTheLargestCache()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.AmdDualChipletAsymmetricCache());

        Assert.Equal(2, layout.Complexes.Count);
        Assert.Equal(Enumerable.Range(0, 8), layout.PreferredGameCoreIds);
        Assert.Equal(Enumerable.Range(8, 8), layout.BackgroundCoreIds);
    }

    [Fact]
    public void DualChipletRyzen_ReportsTheCacheDifferenceInItsNotes()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.AmdDualChipletAsymmetricCache());

        Assert.Contains(layout.Notes, note => note.Contains("largest last level cache", System.StringComparison.Ordinal));
    }

    [Fact]
    public void DualChipletRyzen_UsesReportedDiesWhenWindowsSuppliesThem()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.AmdDualChipletAsymmetricCache());

        Assert.All(layout.Complexes, complex => Assert.Equal(CoreComplexKind.ProcessorDie, complex.Kind));
    }

    [Fact]
    public void SymmetricDualChiplet_StillPicksOneComplexAndSaysWhy()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.AmdDualChipletSymmetricCache());

        Assert.Equal(8, layout.PreferredGameCoreIds.Count);
        Assert.Contains(layout.Notes, note => note.Contains("equal cache", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SmallLaptop_IsNeverConstrained()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.QuadCoreLaptop());

        // Four cores: confining the game would starve either it or everything else.
        Assert.Equal(4, layout.PreferredGameCoreIds.Count);
        Assert.Empty(layout.BackgroundCoreIds);
    }

    [Fact]
    public void MultipleProcessorGroups_AreCalledOut()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.DualProcessorGroupWorkstation());

        Assert.True(layout.SpansMultipleProcessorGroups);
        Assert.Contains(layout.Notes, note => note.Contains("processor groups", System.StringComparison.Ordinal));
        Assert.Contains(layout.Notes, note => note.Contains("NUMA", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SimultaneousMultithreading_IsReportedAndLeftAlone()
    {
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(MachineFixtures.IntelDesktopEightCore());

        Assert.Contains(
            layout.Notes,
            note => note.Contains("Simultaneous multithreading is enabled", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyTopology_DegradesInsteadOfThrowing()
    {
        CpuTopology empty = new CpuTopologyBuilder().Build();

        CpuLayout layout = CpuTopologyAnalyzer.Analyze(empty);

        Assert.Empty(layout.PreferredGameCoreIds);
        Assert.Single(layout.Notes);
    }

    [Fact]
    public void PreferredAndBackgroundCores_NeverOverlap()
    {
        foreach (CpuTopology topology in AllFixtures())
        {
            CpuLayout layout = CpuTopologyAnalyzer.Analyze(topology);
            Assert.Empty(layout.PreferredGameCoreIds.Intersect(layout.BackgroundCoreIds));
        }
    }

    [Fact]
    public void EveryFixture_ProducesCoreIdsThatExist()
    {
        foreach (CpuTopology topology in AllFixtures())
        {
            CpuLayout layout = CpuTopologyAnalyzer.Analyze(topology);
            HashSet<int> known = topology.PhysicalCores.Select(core => core.CoreId).ToHashSet();

            Assert.All(layout.PreferredGameCoreIds, id => Assert.Contains(id, known));
            Assert.All(layout.BackgroundCoreIds, id => Assert.Contains(id, known));
            Assert.All(layout.Complexes.SelectMany(complex => complex.PhysicalCoreIds),
                id => Assert.Contains(id, known));
        }
    }

    private static IEnumerable<CpuTopology> AllFixtures()
    {
        yield return MachineFixtures.IntelDesktopEightCore();
        yield return MachineFixtures.IntelHybridPerformanceAndEfficiency();
        yield return MachineFixtures.AmdSingleChiplet();
        yield return MachineFixtures.AmdDualChipletAsymmetricCache();
        yield return MachineFixtures.AmdDualChipletSymmetricCache();
        yield return MachineFixtures.QuadCoreLaptop();
        yield return MachineFixtures.DualProcessorGroupWorkstation();
    }
}
