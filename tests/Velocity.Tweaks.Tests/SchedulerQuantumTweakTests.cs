using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.TestSupport;
using Velocity.Tweaks;
using Velocity.Tweaks.Cpu;

namespace Velocity.Tweaks.Tests;

/// <summary>The bit layout is the whole module; if it is wrong, everything else is wrong.</summary>
public sealed class PrioritySeparationTests
{
    [Fact]
    public void WindowsClientDefault_IsDecimalTwo()
    {
        // Short, variable, 3x boost is what a client install already does.
        Assert.Equal(2u, PrioritySeparation.WindowsClientDefault.ToRaw());
    }

    [Fact]
    public void ShortFixedMaximumBoost_IsDecimalTwentySix()
    {
        Assert.Equal(26u, PrioritySeparation.ShortFixedMaximumBoost.ToRaw());
    }

    [Fact]
    public void DecodingTwentySix_GivesShortFixedTripleBoost()
    {
        PrioritySeparation decoded = PrioritySeparation.FromRaw(26u);

        Assert.Equal(QuantumLength.Short, decoded.Length);
        Assert.Equal(QuantumType.Fixed, decoded.Type);
        Assert.Equal(ForegroundBoost.Triple, decoded.Boost);
    }

    [Fact]
    public void DecodingThirtyEight_GivesLongVariableTripleBoost()
    {
        // 38 is the other value the guides circulate; it is the server-like configuration.
        PrioritySeparation decoded = PrioritySeparation.FromRaw(38u);

        Assert.Equal(QuantumLength.Long, decoded.Length);
        Assert.Equal(QuantumType.Variable, decoded.Type);
        Assert.Equal(ForegroundBoost.Triple, decoded.Boost);
    }

    [Fact]
    public void EveryValidEncoding_RoundTrips()
    {
        foreach (ForegroundBoost boost in Enum.GetValues<ForegroundBoost>())
        {
            foreach (QuantumType type in Enum.GetValues<QuantumType>())
            {
                foreach (QuantumLength length in Enum.GetValues<QuantumLength>())
                {
                    var separation = new PrioritySeparation(boost, type, length);
                    Assert.Equal(separation, PrioritySeparation.FromRaw(separation.ToRaw()));
                }
            }
        }
    }

    [Fact]
    public void ReservedBitPatterns_AreClampedRatherThanThrowing()
    {
        // 0b11 is reserved in each field; a machine reporting it must not crash the analyzer.
        PrioritySeparation decoded = PrioritySeparation.FromRaw(0b111111u);

        Assert.Equal(ForegroundBoost.Triple, decoded.Boost);
        Assert.Equal(QuantumType.Fixed, decoded.Type);
        Assert.Equal(QuantumLength.Long, decoded.Length);
    }

    [Fact]
    public void Description_IsReadable()
    {
        Assert.Equal(
            "short, fixed quantums, 3x foreground boost (raw 26)",
            PrioritySeparation.ShortFixedMaximumBoost.ToString());
    }
}

/// <summary>
/// The first production module, driven through the real pipeline: compatibility, detect, snapshot,
/// apply, verify, rollback.
/// </summary>
public sealed class SchedulerQuantumTweakTests
{
    private static readonly StateKey Key =
        RegistryKeys.Value(RegistryKeys.PriorityControl, "Win32PrioritySeparation");

    [Fact]
    public void Descriptor_DoesNotPromiseAnImprovement()
    {
        var tweak = new SchedulerQuantumTweak();

        // The product's rule: a descriptor that promises an FPS number is a bug.
        Assert.True(tweak.Descriptor.BenchmarkRecommended);
        Assert.Contains("Benchmark it", tweak.Descriptor.ExpectedEffect, StringComparison.Ordinal);
        Assert.Contains("already the Windows default", tweak.Descriptor.ExpectedEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_DeclaresElevationAndRestart()
    {
        var tweak = new SchedulerQuantumTweak();

        Assert.True(tweak.Descriptor.RequiresElevation);
        Assert.True(tweak.Descriptor.RequiresRestart);
        Assert.Equal(TweakScope.Persistent, tweak.Descriptor.Scope);
    }

    [Fact]
    public async Task ItDeclaresTheOneKeyItWrites()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out _);

        StateKey declared = Assert.Single(
            await tweak.GetStateKeysAsync(context, CancellationToken.None));

        Assert.Equal(Key, declared);
        Assert.Equal(
            @"registry://HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl#Win32PrioritySeparation",
            declared.ToString());
    }

    [Fact]
    public async Task Compatibility_IsRefusedInsideAVirtualMachine()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out _, MachineKind.VirtualMachine);

        CompatibilityResult result = await tweak.CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public async Task Compatibility_IsSupportedOnADesktop()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out _);

        Assert.True((await tweak.CheckCompatibilityAsync(context, CancellationToken.None)).IsSupported);
    }

    [Fact]
    public void Compatibility_IsRefusedOnAnOlderWindowsBuild()
    {
        var tweak = new SchedulerQuantumTweak();
        SystemProfile profile = MachineFixtures.ProfileFor(
            MachineFixtures.IntelDesktopEightCore(), buildNumber: 18362);

        CompatibilityResult result = CompatibilityEvaluator.Evaluate(
            tweak.Descriptor,
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            new FakePrivilegeContext());

        Assert.Equal(CompatibilityStatus.UnsupportedOperatingSystem, result.Status);
    }

    [Fact]
    public void Compatibility_IsRefusedWithoutTheHelper()
    {
        var tweak = new SchedulerQuantumTweak();
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        CompatibilityResult result = CompatibilityEvaluator.Evaluate(
            tweak.Descriptor,
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            new FakePrivilegeContext(PrivilegeChannel.None));

        Assert.Equal(CompatibilityStatus.ElevationUnavailable, result.Status);
    }

    [Fact]
    public async Task Detect_ReportsTheWindowsDefaultWhenTheValueIsAbsent()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out _);

        TweakObservation observation = await tweak.DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.NotApplied, observation.State);
        Assert.Contains("Windows uses its default", observation.CurrentValueSummary, StringComparison.Ordinal);
        Assert.Equal("(not set)", observation.Details["registry_value"]);
    }

    [Fact]
    public async Task Detect_DecodesAnExistingValue()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(Key, StateValue.FromUInt32(2));

        TweakObservation observation = await tweak.DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.NotApplied, observation.State);
        Assert.Equal("Triple", observation.Details["foreground_boost"]);
        Assert.Equal("2", observation.Details["registry_value"]);
    }

    [Fact]
    public async Task Detect_ReportsAppliedWhenTheMachineAlreadyMatches()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(Key, StateValue.FromUInt32(26));

        TweakObservation observation = await tweak.DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.Applied, observation.State);
    }

    [Fact]
    public async Task Apply_WritesTwentySixAndReportsThatARestartIsNeeded()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);

        ApplyResult result = await tweak.ApplyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.AppliedPendingRestart, result.Outcome);
        Assert.Equal(StateValue.FromUInt32(26), await provider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_DoesNothingWhenTheValueAlreadyMatches()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(Key, StateValue.FromUInt32(26));

        ApplyResult result = await tweak.ApplyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.NoChangeRequired, result.Outcome);
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task Apply_HonoursAnOptionOverride()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(
            out InMemoryStateProvider provider,
            options: new Dictionary<string, string> { [SchedulerQuantumTweak.RawValueOption] = "38" });

        await tweak.ApplyAsync(context, CancellationToken.None);

        // An auto-tune trial can explore other encodings without a code change.
        Assert.Equal(StateValue.FromUInt32(38), await provider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_IgnoresAnOptionOutsideTheEncodableRange()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(
            out InMemoryStateProvider provider,
            options: new Dictionary<string, string> { [SchedulerQuantumTweak.RawValueOption] = "9999" });

        await tweak.ApplyAsync(context, CancellationToken.None);

        Assert.Equal(StateValue.FromUInt32(26), await provider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_ReportsPendingRestartRatherThanClaimingItIsInEffect()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(Key, StateValue.FromUInt32(26));

        VerificationResult result = await tweak.VerifyAsync(context, CancellationToken.None);

        // The kernel reads the value at boot and it cannot be observed from user mode.
        Assert.Equal(VerificationStatus.PendingRestart, result.Status);
        Assert.True(result.IsAcceptable);
    }

    [Fact]
    public async Task Verify_FailsWhenTheWriteDidNotLand()
    {
        var tweak = new SchedulerQuantumTweak();
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(Key, StateValue.FromUInt32(2));

        VerificationResult result = await tweak.VerifyAsync(context, CancellationToken.None);

        Assert.Equal(VerificationStatus.Mismatch, result.Status);
    }

    [Fact]
    public async Task FullPipeline_AppliesThenRestoresAnExistingValue()
    {
        await using EngineHarness harness =
            await EngineHarness.CreateAsync(new ITweak[] { new SchedulerQuantumTweak() });

        harness.RegistryState.Seed(Key, StateValue.FromUInt32(2));

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SchedulerQuantumTweak.TweakId },
                Reason = TransactionReason.ManualTweak,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, applied.Status);
        Assert.Equal(
            StateValue.FromUInt32(26),
            await harness.RegistryState.ReadAsync(Key, CancellationToken.None));

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.Equal(
            StateValue.FromUInt32(2),
            await harness.RegistryState.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task FullPipeline_RollbackRemovesTheValueWhenItDidNotExistBefore()
    {
        await using EngineHarness harness =
            await EngineHarness.CreateAsync(new ITweak[] { new SchedulerQuantumTweak() });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SchedulerQuantumTweak.TweakId },
                Reason = TransactionReason.ManualTweak,
            },
            CancellationToken.None);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        // The user never had this value; restoring must delete it, not write Windows' default back
        // as if the user had set it.
        Assert.True((await harness.RegistryState.ReadAsync(Key, CancellationToken.None)).IsAbsent);
    }

    [Fact]
    public async Task FullPipeline_SurvivesACrashBetweenTheWriteAndTheJournalUpdate()
    {
        await using EngineHarness harness =
            await EngineHarness.CreateAsync(new ITweak[] { new SchedulerQuantumTweak() });

        harness.RegistryState.Seed(Key, StateValue.FromUInt32(2));

        await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SchedulerQuantumTweak.TweakId },
                Reason = TransactionReason.GamingSession,
            },
            CancellationToken.None);

        // Simulate the journal still reporting the transaction as in flight after a kill.
        IReadOnlyList<OptimizationTransaction> recent =
            await harness.Journal.GetRecentTransactionsAsync(1, CancellationToken.None);

        await harness.Journal.UpdateTransactionStatusAsync(
            recent[0].Id, TransactionStatus.Applying, null, CancellationToken.None);

        RecoveryReport report = await harness.Recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.RecoveredTransactionCount);
        Assert.Equal(
            StateValue.FromUInt32(2),
            await harness.RegistryState.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task FullPipeline_IsSkippedWithoutTheHelperRatherThanFailing()
    {
        await using EngineHarness harness = await EngineHarness.CreateAsync(
            new ITweak[] { new SchedulerQuantumTweak() },
            privilegeChannel: PrivilegeChannel.None);

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SchedulerQuantumTweak.TweakId },
                Reason = TransactionReason.ProfileApply,
            },
            CancellationToken.None);

        Assert.Equal(ApplyOutcome.Skipped, Assert.Single(result.Results).Outcome);
        Assert.Equal(0, harness.RegistryState.WriteCount);
    }

    private static TweakContext Context(
        out InMemoryStateProvider provider,
        MachineKind machineKind = MachineKind.Desktop,
        IReadOnlyDictionary<string, string>? options = null)
    {
        provider = new InMemoryStateProvider(RegistryKeys.Scheme);
        SystemProfile profile = MachineFixtures.ProfileFor(
            MachineFixtures.IntelDesktopEightCore(), machineKind);

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new[] { (Abstractions.State.IStateProvider)provider }),
            new FakePrivilegeContext(),
            SchedulerQuantumTweak.TweakId,
            new[] { Key });

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance,
            options);
    }
}
