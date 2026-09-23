using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.State;
using Velocity.Core.Hardware;
using Velocity.Core.Processes;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// The protected list is a safety list: it says what must not be touched, never what should be.
/// </summary>
public sealed class ProcessProtectionClassifierTests
{
    [Theory]
    [InlineData("csrss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("SecurityHealthService.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("BEService.exe")]
    [InlineData("vgc.exe")]
    public void PlatformSecurityAndAntiCheat_AreNeverTouched(string executable)
    {
        Assert.Equal(ProcessProtection.Protected, ProcessProtectionClassifier.Classify(executable));
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("audiodg.exe")]
    [InlineData("discord.exe")]
    [InlineData("rtss.exe")]
    public void ShellAudioAndOverlays_MayBeDePrioritisedButNeverSuspended(string executable)
    {
        Assert.Equal(ProcessProtection.NeverSuspend, ProcessProtectionClassifier.Classify(executable));
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("Spotify.exe")]
    [InlineData("Slack.exe")]
    public void OrdinaryUserSoftware_IsFairGame(string executable)
    {
        Assert.Equal(ProcessProtection.None, ProcessProtectionClassifier.Classify(executable));
    }

    [Fact]
    public void AProcessRunningAsASystemAccount_IsProtectedEvenIfUnrecognised()
    {
        Assert.Equal(
            ProcessProtection.Protected,
            ProcessProtectionClassifier.Classify("something-unknown.exe", isSystemProcess: true));
    }

    [Fact]
    public void AProcessThatCannotBeIdentified_IsProtected()
    {
        // Failing closed: the cost of wrongly touching a platform process is higher than the frames
        // gained by touching one more background application.
        Assert.Equal(ProcessProtection.Protected, ProcessProtectionClassifier.Classify((string?)null));
        Assert.Equal(ProcessProtection.Protected, ProcessProtectionClassifier.Classify("  "));
    }

    [Fact]
    public void ClassificationIgnoresCaseAndPath()
    {
        Assert.Equal(
            ProcessProtection.Protected,
            ProcessProtectionClassifier.Classify(@"C:\Windows\System32\LSASS.EXE"));
    }

    [Fact]
    public void TheListsAreInspectable()
    {
        Assert.NotEmpty(ProcessProtectionClassifier.DescribeNeverTouched());
        Assert.NotEmpty(ProcessProtectionClassifier.DescribeNeverSuspended());
    }
}

/// <summary>
/// Placement decides which cores a game gets. Getting it wrong is worse than doing nothing, so the
/// policy is tested against every machine fixture including the ones where it must decline.
/// </summary>
public sealed class CorePlacementPolicyTests
{
    [Fact]
    public void ASingleComplexDesktop_GetsNoPlacementAtAll()
    {
        PlacementPlan plan = Plan(MachineFixtures.IntelDesktopEightCore(), PlacementIntent.IsolateGame);

        Assert.True(plan.IsNoOp);
        Assert.Contains(plan.Notes, note => note.Contains("no separate group of cores", StringComparison.Ordinal));
    }

    [Fact]
    public void ADualChipletProcessor_PlacesTheGameOnTheLargerCacheComplex()
    {
        PlacementPlan plan = Plan(
            MachineFixtures.AmdDualChipletAsymmetricCache(), PlacementIntent.IsolateGame);

        Assert.True(plan.ConstrainGame);
        Assert.True(plan.ConstrainBackground);
        Assert.Equal(16, plan.GameProcessors.Count);
        Assert.Equal(16, plan.BackgroundProcessors.Count);
        Assert.Empty(plan.GameProcessors.Intersect(plan.BackgroundProcessors));
    }

    [Fact]
    public void AHybridProcessor_PlacesTheGameOnThePerformanceCores()
    {
        PlacementPlan plan = Plan(
            MachineFixtures.IntelHybridPerformanceAndEfficiency(), PlacementIntent.IsolateGame);

        // 8 P-cores with two threads each.
        Assert.Equal(16, plan.GameProcessors.Count);
        Assert.Equal(16, plan.BackgroundProcessors.Count);
    }

    [Fact]
    public void ConfineBackgroundOnly_LeavesTheGameUnconstrained()
    {
        PlacementPlan plan = Plan(
            MachineFixtures.AmdDualChipletAsymmetricCache(), PlacementIntent.ConfineBackgroundOnly);

        Assert.False(plan.ConstrainGame);
        Assert.True(plan.ConstrainBackground);
    }

    [Fact]
    public void Observe_ChangesNothing()
    {
        PlacementPlan plan = Plan(
            MachineFixtures.AmdDualChipletAsymmetricCache(), PlacementIntent.Observe);

        Assert.True(plan.IsNoOp);
        Assert.NotEmpty(plan.GameProcessors);
    }

    [Fact]
    public void AMachineSpanningTwoProcessorGroups_IsDeclinedWithAReason()
    {
        PlacementPlan plan = Plan(
            MachineFixtures.DualProcessorGroupWorkstation(), PlacementIntent.IsolateGame);

        // Affinity masks are group relative; a mask spanning groups would mean something different
        // from what it looks like.
        Assert.True(plan.IsNoOp);
        Assert.Contains(plan.Notes, note => note.Contains("processor group", StringComparison.Ordinal));
    }

    [Fact]
    public void ASmallLaptop_IsNeverConstrained()
    {
        PlacementPlan plan = Plan(MachineFixtures.QuadCoreLaptop(), PlacementIntent.IsolateGame);

        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public void TheBackgroundMaskCoversExactlyTheBackgroundProcessors()
    {
        CpuTopology topology = MachineFixtures.AmdDualChipletAsymmetricCache();
        PlacementPlan plan = Plan(topology, PlacementIntent.IsolateGame);

        ulong expected = CorePlacementPolicy.BuildMask(topology, plan.BackgroundProcessors);

        Assert.Equal(expected, plan.BackgroundAffinityMask);
        Assert.NotEqual(0UL, plan.BackgroundAffinityMask);

        // The game's processors must not appear in the background mask.
        ulong gameMask = CorePlacementPolicy.BuildMask(topology, plan.GameProcessors);
        Assert.Equal(0UL, gameMask & plan.BackgroundAffinityMask);
    }

    [Fact]
    public void EveryPlanExplainsItself()
    {
        foreach (CpuTopology topology in new[]
                 {
                     MachineFixtures.IntelDesktopEightCore(),
                     MachineFixtures.IntelHybridPerformanceAndEfficiency(),
                     MachineFixtures.AmdSingleChiplet(),
                     MachineFixtures.AmdDualChipletAsymmetricCache(),
                     MachineFixtures.QuadCoreLaptop(),
                     MachineFixtures.DualProcessorGroupWorkstation(),
                 })
        {
            PlacementPlan plan = Plan(topology, PlacementIntent.IsolateGame);
            Assert.NotEmpty(plan.Notes);
        }
    }

    private static PlacementPlan Plan(CpuTopology topology, PlacementIntent intent) =>
        CorePlacementPolicy.Create(CpuTopologyAnalyzer.Analyze(topology), intent);
}

/// <summary>
/// The process state provider is what gives per process changes crash safe rollback, so its
/// behaviour around exited and protected processes is the part that matters.
/// </summary>
public sealed class ProcessStateProviderTests
{
    [Fact]
    public async Task Read_CapturesThePriorityOfEveryMatchingProcess()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100),
            ProcessFixtures.Process("chrome.exe", 101, ProcessPriority.BelowNormal),
            ProcessFixtures.Process("spotify.exe", 200));

        ProcessStateProvider provider = Create(inspector, out _);

        StateValue value = await provider.ReadAsync(Key("chrome.exe"), CancellationToken.None);

        Dictionary<string, string> captured = Parse(value);
        Assert.Equal(2, captured.Count);
        Assert.Equal("Normal", captured["100"]);
        Assert.Equal("BelowNormal", captured["101"]);
    }

    [Fact]
    public async Task Read_ReportsAbsentWhenNothingIsRunning()
    {
        ProcessStateProvider provider = Create(new FakeProcessInspector(), out _);

        Assert.True((await provider.ReadAsync(Key("chrome.exe"), CancellationToken.None)).IsAbsent);
    }

    [Fact]
    public async Task Write_AppliesThePriorityToEachRecordedProcess()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100),
            ProcessFixtures.Process("chrome.exe", 101));

        ProcessStateProvider provider = Create(inspector, out FakeProcessController controller);

        await provider.WriteAsync(
            Key("chrome.exe"),
            Json(new Dictionary<string, string> { ["100"] = "BelowNormal", ["101"] = "Idle" }),
            CancellationToken.None);

        Assert.Equal(2, controller.PriorityCalls.Count);
        Assert.Contains((100, ProcessPriority.BelowNormal), controller.PriorityCalls);
        Assert.Contains((101, ProcessPriority.Idle), controller.PriorityCalls);
    }

    [Fact]
    public async Task Write_SkipsAProcessThatHasExited()
    {
        var inspector = new FakeProcessInspector(ProcessFixtures.Process("chrome.exe", 100));
        ProcessStateProvider provider = Create(inspector, out FakeProcessController controller);

        await provider.WriteAsync(
            Key("chrome.exe"),
            Json(new Dictionary<string, string> { ["100"] = "Normal", ["999"] = "Normal" }),
            CancellationToken.None);

        // A process that exited took the modification with it; restoring it is neither possible
        // nor necessary.
        Assert.Equal(100, Assert.Single(controller.PriorityCalls).ProcessId);
    }

    [Fact]
    public async Task Write_RefusesAProtectedProcess()
    {
        var inspector = new FakeProcessInspector(ProcessFixtures.Process("lsass.exe", 100));
        ProcessStateProvider provider = Create(inspector, out FakeProcessController controller);

        await provider.WriteAsync(
            Key("lsass.exe"),
            Json(new Dictionary<string, string> { ["100"] = "Idle" }),
            CancellationToken.None);

        // The check lives at the provider so a future module cannot reach a protected process by
        // forgetting to ask.
        Assert.Empty(controller.PriorityCalls);
    }

    [Fact]
    public async Task Write_OfAnAbsentValueDoesNothing()
    {
        var inspector = new FakeProcessInspector(ProcessFixtures.Process("chrome.exe", 100));
        ProcessStateProvider provider = Create(inspector, out FakeProcessController controller);

        await provider.WriteAsync(Key("chrome.exe"), StateValue.Absent, CancellationToken.None);

        Assert.Empty(controller.PriorityCalls);
    }

    [Fact]
    public async Task AffinityRoundTrips()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100, affinityMask: 0xFF));

        ProcessStateProvider provider = Create(inspector, out FakeProcessController controller);

        StateValue captured = await provider.ReadAsync(
            new StateKey(ProcessStateProvider.Scheme, "chrome.exe", ProcessStateProvider.AffinityItem),
            CancellationToken.None);

        await provider.WriteAsync(
            new StateKey(ProcessStateProvider.Scheme, "chrome.exe", ProcessStateProvider.AffinityItem),
            Json(new Dictionary<string, string> { ["100"] = "15" }),
            CancellationToken.None);

        Assert.Equal("255", Parse(captured)["100"]);
        Assert.Equal((100, 15UL), Assert.Single(controller.AffinityCalls));
    }

    [Fact]
    public void ProcessStateNeverNeedsElevation()
    {
        ProcessStateProvider provider = Create(new FakeProcessInspector(), out _);

        // Everything it touches is a process the current user already owns.
        Assert.False(provider.RequiresElevation(Key("chrome.exe")));
    }

    private static ProcessStateProvider Create(
        FakeProcessInspector inspector,
        out FakeProcessController controller)
    {
        controller = new FakeProcessController(inspector);
        return new ProcessStateProvider(inspector, controller, NullLogger<ProcessStateProvider>.Instance);
    }

    private static StateKey Key(string executable) =>
        new(ProcessStateProvider.Scheme, executable, ProcessStateProvider.PriorityItem);

    private static StateValue Json(Dictionary<string, string> values) =>
        new(StateValueKind.Json, JsonSerializer.Serialize(values));

    private static Dictionary<string, string> Parse(StateValue value) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(value.Data ?? "{}")
        ?? new Dictionary<string, string>();
}
