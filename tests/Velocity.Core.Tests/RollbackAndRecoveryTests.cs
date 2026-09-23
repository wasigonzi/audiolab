using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Transactions;
using Velocity.Data.Repositories;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// Tests for the two promises that make aggressive optimization defensible: anything applied can be
/// put back, and a crash mid-apply is repaired on the next launch.
/// </summary>
public sealed class RollbackAndRecoveryTests
{
    private static readonly StateKey Key = new("memory", "test/path", "Value");

    [Fact]
    public async Task Rollback_RestoresTheCapturedValue()
    {
        var tweak = new ScriptedTweak("test.rollback", Key, StateValue.FromUInt32(99));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(11));

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        RollbackResult rollback =
            await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.True(rollback.Succeeded);
        Assert.Equal(1, rollback.RestoredKeyCount);
        Assert.Equal(
            StateValue.FromUInt32(11),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_DeletesAValueThatDidNotExistBefore()
    {
        var tweak = new ScriptedTweak("test.absent", Key, StateValue.FromUInt32(5));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        // Restoring "absent" must delete the item, not write a guessed default.
        Assert.True((await harness.StateProvider.ReadAsync(Key, CancellationToken.None)).IsAbsent);
    }

    [Fact]
    public async Task Rollback_IsIdempotent()
    {
        var tweak = new ScriptedTweak("test.twice", Key, StateValue.FromUInt32(3));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(1));

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);
        RollbackResult second =
            await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(
            StateValue.FromUInt32(1),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_UndoesStepsInReverseOrder()
    {
        var order = new List<string>();
        var firstKey = new StateKey("memory", "test/path", "First");
        var secondKey = new StateKey("memory", "test/path", "Second");

        var first = new ScriptedTweak("test.a", firstKey, StateValue.FromUInt32(1))
        {
            OnRollback = order.Add,
        };
        var second = new ScriptedTweak("test.b", secondKey, StateValue.FromUInt32(2))
        {
            OnRollback = order.Add,
        };

        await using EngineHarness harness = await EngineHarness.CreateAsync(new ITweak[] { first, second });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { "test.a", "test.b" },
                Reason = TransactionReason.ProfileApply,
            },
            CancellationToken.None);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        OptimizationTransaction? transaction =
            await harness.Journal.GetTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.All(transaction!.Steps, step => Assert.True(step.RolledBack));
        Assert.Equal(TransactionStatus.RolledBack, transaction.Status);

        // Later steps are undone first: an earlier step may have captured state a later one relies on.
        Assert.Equal(new[] { "test.b", "test.a" }, order);
    }

    [Fact]
    public async Task Rollback_InvokesACustomRollbackWithItsStoredPayload()
    {
        var tweak = new ScriptedTweak(
            "test.custom",
            Key,
            async (context, token) =>
            {
                context.SetRollbackPayload("""{"suspended":[1234]}""");
                await context.State.WriteAsync(Key, StateValue.FromUInt32(1), token);
                return ApplyResult.Applied("Suspended a process.", Key);
            });

        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.True(tweak.CustomRollbackInvoked);
        Assert.Equal("""{"suspended":[1234]}""", tweak.ObservedRollbackPayload);
    }

    [Fact]
    public async Task RollbackLast_ReversesTheMostRecentTransaction()
    {
        var tweak = new ScriptedTweak("test.last", Key, StateValue.FromUInt32(8));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(2));

        await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        RollbackResult? result = await harness.Rollback.RollbackLastAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(
            StateValue.FromUInt32(2),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task RollbackLast_ReportsNothingToDoOnACleanMachine()
    {
        await using EngineHarness harness = await EngineHarness.CreateAsync(Array.Empty<ITweak>());

        Assert.Null(await harness.Rollback.RollbackLastAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RollbackTweak_ReversesOneTweakAndForgetsItWasApplied()
    {
        var tweak = new ScriptedTweak("test.single", Key, StateValue.FromUInt32(6));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(3));

        await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        RollbackResult? result = await harness.Rollback.RollbackTweakAsync("test.single", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(
            StateValue.FromUInt32(3),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
        Assert.Empty(await harness.AppliedTweaks.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_RollsBackATransactionLeftInFlightByACrash()
    {
        var tweak = new ScriptedTweak("test.crash", Key, StateValue.FromUInt32(77));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(12));

        // Simulate a process kill between the write and the journal update: the state has changed
        // and the transaction is still marked as applying.
        Guid transactionId = await SimulateInterruptedApplyAsync(harness, tweak);

        RecoveryReport report = await harness.Recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.RecoveredTransactionCount);
        Assert.Empty(report.FailedTransactionIds);
        Assert.Equal(
            StateValue.FromUInt32(12),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));

        OptimizationTransaction? transaction =
            await harness.Journal.GetTransactionAsync(transactionId, CancellationToken.None);
        Assert.Equal(TransactionStatus.RolledBack, transaction!.Status);
    }

    [Fact]
    public async Task Recovery_DoesNothingWhenEveryTransactionCompleted()
    {
        var tweak = new ScriptedTweak("test.clean", Key, StateValue.FromUInt32(1));
        await using EngineHarness harness = await EngineHarness.CreateAsync(new[] { tweak });

        await harness.Engine.ApplyAsync(Request(tweak), CancellationToken.None);
        RecoveryReport report = await harness.Recovery.RecoverAsync(CancellationToken.None);

        Assert.True(report.NothingToDo);
    }

    [Fact]
    public async Task Recovery_LeavesTheMachineAloneWhenTheCrashHappenedBeforeAnyWrite()
    {
        await using EngineHarness harness = await EngineHarness.CreateAsync(Array.Empty<ITweak>());
        harness.StateProvider.Seed(Key, StateValue.FromUInt32(4));

        var transactionId = Guid.NewGuid();
        await harness.Journal.CreateTransactionAsync(
            new OptimizationTransaction
            {
                Id = transactionId,
                Status = TransactionStatus.Pending,
                Reason = TransactionReason.GamingSession,
                HardwareFingerprint = "fingerprint",
                StartedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        RecoveryReport report = await harness.Recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.RecoveredTransactionCount);
        Assert.Equal(
            StateValue.FromUInt32(4),
            await harness.StateProvider.ReadAsync(Key, CancellationToken.None));
    }

    /// <summary>
    /// Reproduces the on-disk state a crash leaves behind: a snapshot committed, the machine
    /// changed, and the transaction still marked as applying.
    /// </summary>
    private static async Task<Guid> SimulateInterruptedApplyAsync(EngineHarness harness, ScriptedTweak tweak)
    {
        var transactionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        await harness.Journal.CreateTransactionAsync(
            new OptimizationTransaction
            {
                Id = transactionId,
                Status = TransactionStatus.Applying,
                Reason = TransactionReason.GamingSession,
                HardwareFingerprint = "fingerprint",
                StartedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        StateValue captured = await harness.StateProvider.ReadAsync(tweak.Key, CancellationToken.None);

        await harness.Journal.SaveSnapshotAsync(
            new StateSnapshot
            {
                Id = snapshotId,
                TransactionId = transactionId,
                TweakId = tweak.Descriptor.Id,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Entries = new[] { new SnapshotEntry(tweak.Key, captured) },
            },
            CancellationToken.None);

        await harness.Journal.AddStepAsync(
            new TransactionStep
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Ordinal = 0,
                TweakId = tweak.Descriptor.Id,
                TweakDefinitionVersion = 1,
                SnapshotId = snapshotId,
                StartedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        await harness.StateProvider.WriteAsync(tweak.Key, tweak.TargetValue, CancellationToken.None);

        return transactionId;
    }

    private static OptimizationRequest Request(ITweak tweak) => new()
    {
        TweakIds = new[] { tweak.Descriptor.Id },
        Reason = TransactionReason.ManualTweak,
    };
}
