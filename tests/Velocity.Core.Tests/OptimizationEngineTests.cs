using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Diagnostics;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Transactions;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// End to end tests of the apply pipeline: compatibility, snapshot, apply, verify, journal.
/// </summary>
public sealed class OptimizationEngineTests
{
    private static readonly StateKey Key = new("memory", "test/path", "Value");

    [Fact]
    public async Task Apply_WritesTheValueAndMarksTheTransactionApplied()
    {
        var tweak = new ScriptedTweak("test.write", Key, StateValue.FromUInt32(42));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TransactionStatus.Applied, result.Status);
        Assert.Equal(
            StateValue.FromUInt32(42),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_CapturesTheOriginalValueBeforeWriting()
    {
        var tweak = new ScriptedTweak("test.write", Key, StateValue.FromUInt32(42));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(7));

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        OptimizationTransaction? transaction =
            await harness.Journal.GetTransactionAsync(result.TransactionId, CancellationToken.None);
        Guid snapshotId = Assert.Single(transaction!.Steps).SnapshotId!.Value;
        StateSnapshot? snapshot = await harness.Journal.GetSnapshotAsync(snapshotId, CancellationToken.None);

        SnapshotEntry entry = Assert.Single(snapshot!.Entries);
        Assert.Equal(Key, entry.Key);
        Assert.Equal(StateValue.FromUInt32(7), entry.Value);
    }

    [Fact]
    public async Task Apply_RecordsAbsenceWhenTheValueDidNotExist()
    {
        var tweak = new ScriptedTweak("test.write", Key, StateValue.FromUInt32(1));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        OptimizationTransaction? transaction =
            await harness.Journal.GetTransactionAsync(result.TransactionId, CancellationToken.None);
        StateSnapshot? snapshot = await harness.Journal.GetSnapshotAsync(
            transaction!.Steps[0].SnapshotId!.Value, CancellationToken.None);

        Assert.True(Assert.Single(snapshot!.Entries).Value.IsAbsent);
    }

    [Fact]
    public async Task Apply_SkipsAnIncompatibleTweakWithoutFailingTheRun()
    {
        var tweak = new ScriptedTweak("test.incompatible", Key, StateValue.FromUInt32(1))
        {
            Compatibility = CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware, "No such hardware here."),
        };

        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, result.Status);
        Assert.Equal(ApplyOutcome.Skipped, Assert.Single(result.Results).Outcome);
        Assert.Equal(0, harness.StateProvider.WriteCount);
    }

    [Fact]
    public async Task Apply_RollsBackEverythingWhenAVerificationFails()
    {
        var first = new ScriptedTweak("test.first", Key, StateValue.FromUInt32(10));
        var secondKey = new StateKey("memory", "test/path", "Second");
        var second = new ScriptedTweak("test.second", secondKey, StateValue.FromUInt32(20))
        {
            Verification = VerificationResult.Mismatch("The machine did not accept the change."),
        };

        await using EngineHarness harness = await EngineHarness.CreateAsync(new ITweak[] { first, second });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(1));
        harness.StateProvider.Seed(secondKey, StateValue.FromUInt32(2));

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { "test.first", "test.second" },
                Reason = TransactionReason.ProfileApply,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(
            StateValue.FromUInt32(1),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
        Assert.Equal(
            StateValue.FromUInt32(2),
            await harness.StateProvider.ReadAsync(secondKey, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_ContinuesPastAFailureWhenTheRunIsNotAtomic()
    {
        var failing = new ScriptedTweak(
            "test.failing", Key, (_, _) => Task.FromResult(ApplyResult.Failed("Nope.")));
        var secondKey = new StateKey("memory", "test/path", "Second");
        var working = new ScriptedTweak("test.working", secondKey, StateValue.FromUInt32(5));

        await using EngineHarness harness = await EngineHarness.CreateAsync(new ITweak[] { failing, working });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { "test.failing", "test.working" },
                Reason = TransactionReason.ProfileApply,
                AtomicAllOrNothing = false,
            },
            CancellationToken.None);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(
            StateValue.FromUInt32(5),
            await harness.StateProvider.ReadAsync(secondKey, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_RejectsAWriteToAKeyTheTweakDidNotDeclare()
    {
        var undeclared = new StateKey("memory", "test/path", "Undeclared");
        var tweak = new ScriptedTweak(
            "test.undeclared",
            Key,
            async (context, token) =>
            {
                await context.State.WriteAsync(undeclared, StateValue.FromUInt32(1), token);
                return ApplyResult.Applied("Should never get here.");
            });

        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        // An undeclared write was never snapshotted, so it could not be rolled back. The engine
        // treats that as a defect in the module and fails the transaction.
        Assert.Equal(ApplyOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Equal(StateValue.Absent, await harness.StateProvider.ReadAsync(undeclared, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_RecordsTheTweakAsApplied()
    {
        var tweak = new ScriptedTweak("test.applied", Key, StateValue.FromUInt32(3));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        IReadOnlyList<Velocity.Data.Repositories.AppliedTweakRecord> applied =
            await harness.AppliedTweaks.GetAllAsync(CancellationToken.None);

        Assert.Equal("test.applied", Assert.Single(applied).TweakId);
    }

    [Fact]
    public async Task Apply_WritesAnAuditTrail()
    {
        var tweak = new ScriptedTweak("test.audit", Key, StateValue.FromUInt32(3));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);

        IReadOnlyList<OperationAuditRecord> records =
            await harness.AuditRepository.GetByTransactionAsync(result.TransactionId, CancellationToken.None);

        Assert.Contains(records, record => record.Action == "transaction-open");
        Assert.Contains(records, record => record.Action == "apply" && record.Target == "test.audit");
        Assert.Contains(records, record => record.Action == "transaction-close");
    }

    [Fact]
    public async Task Detect_ReadsTheMachineWithoutChangingIt()
    {
        var tweak = new ScriptedTweak("test.detect", Key, StateValue.FromUInt32(9));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(4));

        IReadOnlyList<TweakRunResult> results = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal("4", Assert.Single(results).Observation!.CurrentValueSummary);
        Assert.Equal(0, harness.StateProvider.WriteCount);
        Assert.Empty(await harness.Journal.GetRecentTransactionsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_SkipsATweakThatIsNotInThisBuild()
    {
        await using EngineHarness harness = await EngineHarness.CreateAsync(Array.Empty<ITweak>());

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { "not.in.this.build" },
                Reason = TransactionReason.ManualTweak,
            },
            CancellationToken.None);

        Assert.Equal(ApplyOutcome.Skipped, Assert.Single(result.Results).Outcome);
        Assert.Equal(TransactionStatus.Applied, result.Status);
    }

    private static OptimizationRequest Request(ITweak tweak) => new()
    {
        TweakIds = new[] { tweak.Descriptor.Id },
        Reason = TransactionReason.ManualTweak,
    };
}
