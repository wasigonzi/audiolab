using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.Processes;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.TestSupport;
using Velocity.Tweaks.Cpu;

namespace Velocity.Tweaks.Tests;

/// <summary>Background de-prioritisation, driven through the real engine.</summary>
public sealed class BackgroundProcessPriorityTweakTests
{
    [Fact]
    public async Task ItDeclaresOneKeyPerDistinctExecutable()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100),
            ProcessFixtures.Process("chrome.exe", 101),
            ProcessFixtures.Process("spotify.exe", 200));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(inspector, out _);

        IReadOnlyList<StateKey> keys = await tweak.GetStateKeysAsync(context, CancellationToken.None);

        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(ProcessStateProvider.PriorityItem, key.Item));
    }

    [Fact]
    public async Task ItNeverTargetsAProtectedProcess()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("lsass.exe", 100),
            ProcessFixtures.Process("MsMpEng.exe", 101),
            ProcessFixtures.Process("EasyAntiCheat.exe", 102),
            ProcessFixtures.Process("chrome.exe", 200));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(inspector, out _);

        IReadOnlyList<StateKey> keys = await tweak.GetStateKeysAsync(context, CancellationToken.None);

        Assert.Equal("chrome.exe", Assert.Single(keys).Path);
    }

    [Fact]
    public async Task ItLeavesTheForegroundProcessAndTheGameAlone()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("game.exe", 100, isForeground: true),
            ProcessFixtures.Process("launcher.exe", 101),
            ProcessFixtures.Process("chrome.exe", 200));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(
            inspector,
            out _,
            options: new Dictionary<string, string>
            {
                [BackgroundProcessPriorityTweak.GameExecutableOption] = "launcher.exe",
            });

        IReadOnlyList<StateKey> keys = await tweak.GetStateKeysAsync(context, CancellationToken.None);

        Assert.Equal("chrome.exe", Assert.Single(keys).Path);
    }

    [Fact]
    public async Task ItNeverRaisesAPriorityOrTouchesOneAlreadyLowered()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("already-low.exe", 100, ProcessPriority.Idle),
            ProcessFixtures.Process("also-low.exe", 101, ProcessPriority.BelowNormal),
            ProcessFixtures.Process("normal.exe", 102));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(inspector, out _);

        IReadOnlyList<StateKey> keys = await tweak.GetStateKeysAsync(context, CancellationToken.None);

        Assert.Equal("normal.exe", Assert.Single(keys).Path);
    }

    [Fact]
    public async Task ItRefusesToTouchARealTimeProcess()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("audio-workstation.exe", 100, ProcessPriority.RealTime));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(inspector, out _);

        Assert.Empty(await tweak.GetStateKeysAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Detect_ReportsNothingToDoOnAQuietMachine()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100, ProcessPriority.BelowNormal));

        var tweak = new BackgroundProcessPriorityTweak(inspector);
        TweakContext context = Context(inspector, out _);

        TweakObservation observation = await tweak.DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.Applied, observation.State);
    }

    [Fact]
    public async Task FullPipeline_LowersThenRestoresEveryBackgroundProcess()
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("chrome.exe", 100),
            ProcessFixtures.Process("chrome.exe", 101, ProcessPriority.AboveNormal),
            ProcessFixtures.Process("lsass.exe", 300));

        var controller = new FakeProcessController(inspector);
        var provider = new ProcessStateProvider(
            inspector, controller, NullLogger<ProcessStateProvider>.Instance);

        await using EngineHarness harness = await EngineHarness.CreateAsync(
            new ITweak[] { new BackgroundProcessPriorityTweak(inspector) },
            extraProviders: new IStateProvider[] { provider });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { BackgroundProcessPriorityTweak.TweakId },
                Reason = TransactionReason.GamingSession,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, applied.Status);
        Assert.All(
            inspector.Processes.Where(process => process.ExecutableName == "chrome.exe"),
            process => Assert.Equal(ProcessPriority.BelowNormal, process.Priority));

        // lsass was never a candidate and must be untouched.
        Assert.Equal(
            ProcessPriority.Normal,
            inspector.Processes.Single(process => process.ExecutableName == "lsass.exe").Priority);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.Equal(
            ProcessPriority.Normal,
            inspector.Processes.Single(process => process.ProcessId == 100).Priority);
        Assert.Equal(
            ProcessPriority.AboveNormal,
            inspector.Processes.Single(process => process.ProcessId == 101).Priority);
    }

    private static TweakContext Context(
        IProcessInspector inspector,
        out FakeProcessController controller,
        IReadOnlyDictionary<string, string>? options = null)
    {
        controller = new FakeProcessController((FakeProcessInspector)inspector);
        var provider = new ProcessStateProvider(
            inspector, controller, NullLogger<ProcessStateProvider>.Instance);

        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            BackgroundProcessPriorityTweak.TweakId,
            Array.Empty<StateKey>());

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance,
            options);
    }
}

/// <summary>Core placement, including every case where it must decline.</summary>
public sealed class GameCorePlacementTweakTests
{
    [Fact]
    public async Task ItDeclinesOnASingleComplexProcessor()
    {
        (GameCorePlacementTweak tweak, TweakContext context, _) =
            Create(MachineFixtures.IntelDesktopEightCore());

        CompatibilityResult result = await tweak.CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public async Task ItIsSupportedOnADualChipletProcessor()
    {
        (GameCorePlacementTweak tweak, TweakContext context, _) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        Assert.True((await tweak.CheckCompatibilityAsync(context, CancellationToken.None)).IsSupported);
    }

    [Fact]
    public async Task ItGivesTheGameACpuSetAndNotAHardMask()
    {
        (GameCorePlacementTweak tweak, TweakContext context, FakeProcessController controller) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await tweak.ApplyAsync(context, CancellationToken.None);

        // A hard mask on a game cannot be overridden by the scheduler if the choice is wrong.
        (int processId, IReadOnlyList<int> processors) = Assert.Single(controller.CpuSetCalls);
        Assert.Equal(1000, processId);
        Assert.Equal(16, processors.Count);
        Assert.DoesNotContain(controller.AffinityCalls, call => call.ProcessId == 1000);
    }

    [Fact]
    public async Task ItConfinesBackgroundProcessesWithAHardMask()
    {
        (GameCorePlacementTweak tweak, TweakContext context, FakeProcessController controller) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await tweak.ApplyAsync(context, CancellationToken.None);

        Assert.Contains(controller.AffinityCalls, call => call.ProcessId == 2000);
        Assert.All(
            controller.AffinityCalls,
            call => Assert.NotEqual(0UL, call.Mask));
    }

    [Fact]
    public async Task ItDoesNotConfineProtectedProcesses()
    {
        (GameCorePlacementTweak tweak, TweakContext context, FakeProcessController controller) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await tweak.ApplyAsync(context, CancellationToken.None);

        Assert.DoesNotContain(controller.AffinityCalls, call => call.ProcessId == 3000);
    }

    [Fact]
    public async Task Verify_AdmitsThatACpuSetCannotBeReadBack()
    {
        (GameCorePlacementTweak tweak, TweakContext context, _) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache(), confineBackground: false);

        VerificationResult result = await tweak.VerifyAsync(context, CancellationToken.None);

        Assert.Equal(VerificationStatus.NotVerifiable, result.Status);
        Assert.True(result.IsAcceptable);
    }

    [Fact]
    public async Task Rollback_ClearsTheCpuSetsItApplied()
    {
        (GameCorePlacementTweak tweak, TweakContext context, FakeProcessController controller) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await tweak.ApplyAsync(context, CancellationToken.None);
        await tweak.RollbackAsync(context, context.RollbackPayload, CancellationToken.None);

        Assert.Equal(1000, Assert.Single(controller.ClearedCpuSets));
    }

    [Fact]
    public async Task Rollback_IsSafeWhenApplyNeverRan()
    {
        (GameCorePlacementTweak tweak, TweakContext context, FakeProcessController controller) =
            Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await tweak.RollbackAsync(context, rollbackPayload: null, CancellationToken.None);

        Assert.Empty(controller.ClearedCpuSets);
    }

    private static (GameCorePlacementTweak Tweak, TweakContext Context, FakeProcessController Controller)
        Create(CpuTopology topology, bool confineBackground = true)
    {
        var inspector = new FakeProcessInspector(
            ProcessFixtures.Process("game.exe", 1000, isForeground: true),
            ProcessFixtures.Process("chrome.exe", 2000, affinityMask: 0xFFFFFFFF),
            ProcessFixtures.Process("MsMpEng.exe", 3000));

        var controller = new FakeProcessController(inspector);
        var provider = new ProcessStateProvider(
            inspector, controller, NullLogger<ProcessStateProvider>.Instance);

        SystemProfile profile = MachineFixtures.ProfileFor(topology);
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(topology);
        PlacementPlan plan = CorePlacementPolicy.Create(layout, PlacementIntent.IsolateGame);

        var declared = confineBackground
            ? new[]
            {
                new StateKey(ProcessStateProvider.Scheme, "chrome.exe", ProcessStateProvider.AffinityItem),
            }
            : Array.Empty<StateKey>();

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            GameCorePlacementTweak.TweakId,
            declared);

        var options = new Dictionary<string, string>
        {
            [GameCorePlacementTweak.GameExecutableOption] = "game.exe",
            [GameCorePlacementTweak.IntentOption] = confineBackground
                ? nameof(PlacementIntent.IsolateGame)
                : nameof(PlacementIntent.Observe),
        };

        _ = plan;

        return (
            new GameCorePlacementTweak(inspector, controller),
            new TweakContext(profile, layout, accessor, new FakePrivilegeContext(), NullLogger.Instance, options),
            controller);
    }
}
