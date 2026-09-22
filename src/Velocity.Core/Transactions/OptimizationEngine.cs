using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Auditing;
using Velocity.Core.State;
using Velocity.Core.Tweaks;
using Velocity.Data.Repositories;

namespace Velocity.Core.Transactions;

/// <summary>Applies tweaks as a journalled, reversible transaction.</summary>
public interface IOptimizationEngine
{
    /// <summary>Runs the full pipeline for every requested tweak.</summary>
    /// <param name="request">What to apply.</param>
    /// <param name="cancellationToken">Token used to abort the run.</param>
    /// <returns>The outcome of the run.</returns>
    Task<OptimizationRunResult> ApplyAsync(OptimizationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs detection only, without opening a transaction or writing anything.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the sweep.</param>
    /// <returns>Every tweak with its compatibility and current observed state.</returns>
    Task<IReadOnlyList<TweakRunResult>> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The transaction coordinator.
/// </summary>
/// <remarks>
/// <para>
/// The ordering of the steps below is the safety property of the whole product, so it is stated
/// explicitly. For each tweak:
/// </para>
/// <list type="number">
/// <item>compatibility is evaluated, and an unsupported tweak is skipped without touching anything;</item>
/// <item>the current value of every declared key is read and <b>committed to the journal</b>;</item>
/// <item>only then is apply allowed to write;</item>
/// <item>the result is verified by re-reading the machine, not by trusting the write;</item>
/// <item>the step is journalled with its outcome.</item>
/// </list>
/// <para>
/// Because the snapshot reaches disk before the first write, a crash at any point leaves enough
/// information on disk to undo whatever had been done. There is no window in which a change exists
/// on the machine but not in the journal.
/// </para>
/// </remarks>
public sealed class OptimizationEngine : IOptimizationEngine
{
    private const string ModuleName = "Core.OptimizationEngine";

    private readonly ITweakRegistry _registry;
    private readonly ITweakContextFactory _contextFactory;
    private readonly ISystemProfileProvider _profileProvider;
    private readonly ITransactionJournal _journal;
    private readonly IAppliedTweakRepository _appliedTweaks;
    private readonly IAuditSink _audit;
    private readonly IRollbackEngine _rollback;
    private readonly ILogger<OptimizationEngine> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the engine.</summary>
    /// <param name="registry">Tweak catalogue.</param>
    /// <param name="contextFactory">Factory for tweak execution contexts.</param>
    /// <param name="profileProvider">Source of the current machine profile.</param>
    /// <param name="journal">Transaction journal.</param>
    /// <param name="appliedTweaks">Record of what is currently applied.</param>
    /// <param name="audit">Audit sink.</param>
    /// <param name="rollback">Rollback engine used when a step fails.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injected so tests can control timestamps.</param>
    public OptimizationEngine(
        ITweakRegistry registry,
        ITweakContextFactory contextFactory,
        ISystemProfileProvider profileProvider,
        ITransactionJournal journal,
        IAppliedTweakRepository appliedTweaks,
        IAuditSink audit,
        IRollbackEngine rollback,
        ILogger<OptimizationEngine> logger,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _appliedTweaks = appliedTweaks ?? throw new ArgumentNullException(nameof(appliedTweaks));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TweakRunResult>> DetectAsync(CancellationToken cancellationToken)
    {
        var results = new List<TweakRunResult>(_registry.All.Count);

        foreach (ITweak tweak in _registry.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (TweakContext context, _) =
                await _contextFactory.CreateAsync(tweak, null, cancellationToken).ConfigureAwait(false);

            CompatibilityResult compatibility =
                await EvaluateCompatibilityAsync(tweak, context, cancellationToken).ConfigureAwait(false);

            TweakObservation? observation = null;
            string message = compatibility.Reason;

            if (compatibility.IsSupported)
            {
                try
                {
                    observation = await tweak.DetectAsync(context, cancellationToken).ConfigureAwait(false);
                    message = observation.CurrentValueSummary;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Detection failed for tweak {TweakId}.", tweak.Descriptor.Id);
                    message = $"Detection failed: {ex.Message}";
                }
            }

            results.Add(new TweakRunResult
            {
                TweakId = tweak.Descriptor.Id,
                Compatibility = compatibility,
                Observation = observation,
                Message = message,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<OptimizationRunResult> ApplyAsync(
        OptimizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();
        string fingerprint = await GetFingerprintAsync(cancellationToken).ConfigureAwait(false);

        var transaction = new OptimizationTransaction
        {
            Id = transactionId,
            Status = TransactionStatus.Pending,
            Reason = request.Reason,
            ProfileId = request.ProfileId,
            SessionId = request.SessionId,
            HardwareFingerprint = fingerprint,
            StartedAtUtc = startedAt,
        };

        await _journal.CreateTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await _audit.RecordAsync(
            AuditRecordFactory.Success(ModuleName, "transaction-open", transactionId.ToString(), transactionId),
            cancellationToken).ConfigureAwait(false);

        var results = new List<TweakRunResult>(request.TweakIds.Count);
        bool anyApplied = false;
        bool failed = false;
        int ordinal = 0;

        await _journal.UpdateTransactionStatusAsync(
            transactionId, TransactionStatus.Applying, null, cancellationToken).ConfigureAwait(false);

        foreach (string tweakId in request.TweakIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ITweak? tweak = _registry.Find(tweakId);
            if (tweak is null)
            {
                results.Add(new TweakRunResult
                {
                    TweakId = tweakId,
                    Compatibility = CompatibilityResult.Unsupported(
                        CompatibilityStatus.Unknown, "Not present in this build."),
                    Outcome = ApplyOutcome.Skipped,
                    Message = $"Tweak '{tweakId}' is not present in this build and was skipped.",
                });
                continue;
            }

            request.Options.TryGetValue(tweakId, out IReadOnlyDictionary<string, string>? options);

            StepExecution execution = await RunStepAsync(
                transactionId, ordinal, tweak, options, cancellationToken).ConfigureAwait(false);

            results.Add(execution.Result);
            ordinal++;

            if (execution.Applied)
            {
                anyApplied = true;
            }

            if (!execution.Succeeded)
            {
                failed = true;
                if (request.AtomicAllOrNothing)
                {
                    break;
                }
            }
        }

        TransactionStatus finalStatus;

        if (failed && request.AtomicAllOrNothing && anyApplied)
        {
            RollbackResult rollback = await _rollback
                .RollbackTransactionAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);

            finalStatus = rollback.Succeeded
                ? TransactionStatus.FailedAndRolledBack
                : TransactionStatus.RollbackFailed;
        }
        else if (failed && !anyApplied)
        {
            finalStatus = TransactionStatus.FailedAndRolledBack;
            await _journal.UpdateTransactionStatusAsync(
                transactionId, finalStatus, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            finalStatus = TransactionStatus.Applied;
            await _journal.UpdateTransactionStatusAsync(
                transactionId, finalStatus, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        await _audit.RecordAsync(
            AuditRecordFactory.Success(
                ModuleName,
                "transaction-close",
                transactionId.ToString(),
                transactionId,
                newValue: finalStatus.ToString()),
            cancellationToken).ConfigureAwait(false);

        return new OptimizationRunResult
        {
            TransactionId = transactionId,
            Status = finalStatus,
            Results = results,
        };
    }

    private readonly record struct StepExecution(TweakRunResult Result, bool Succeeded, bool Applied);

    private async Task<StepExecution> RunStepAsync(
        Guid transactionId,
        int ordinal,
        ITweak tweak,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken)
    {
        string tweakId = tweak.Descriptor.Id;
        var stepId = Guid.NewGuid();
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        (TweakContext context, TransactionalStateAccessor accessor) =
            await _contextFactory.CreateAsync(tweak, options, cancellationToken).ConfigureAwait(false);

        CompatibilityResult compatibility =
            await EvaluateCompatibilityAsync(tweak, context, cancellationToken).ConfigureAwait(false);

        var step = new TransactionStep
        {
            Id = stepId,
            TransactionId = transactionId,
            Ordinal = ordinal,
            TweakId = tweakId,
            TweakDefinitionVersion = tweak.Descriptor.DefinitionVersion,
            StartedAtUtc = startedAt,
        };

        await _journal.AddStepAsync(step, cancellationToken).ConfigureAwait(false);

        if (!compatibility.IsSupported)
        {
            step = step with
            {
                ApplyOutcome = ApplyOutcome.Skipped,
                Message = compatibility.Reason,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
            };
            await _journal.UpdateStepAsync(step, cancellationToken).ConfigureAwait(false);

            return new StepExecution(
                new TweakRunResult
                {
                    TweakId = tweakId,
                    Compatibility = compatibility,
                    Outcome = ApplyOutcome.Skipped,
                    Message = compatibility.Reason,
                },
                // An incompatible tweak is not a failure: the machine simply does not have the
                // feature, and the rest of the profile should still apply.
                Succeeded: true,
                Applied: false);
        }

        TweakObservation? observation = null;

        try
        {
            observation = await tweak.DetectAsync(context, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<StateKey> declaredKeys =
                await tweak.GetStateKeysAsync(context, cancellationToken).ConfigureAwait(false);
            Guid? snapshotId = null;

            if (declaredKeys.Count > 0)
            {
                StateSnapshot snapshot = await CaptureSnapshotAsync(
                    transactionId, tweakId, declaredKeys, context, cancellationToken).ConfigureAwait(false);

                // Committed before any write: this is the point of no information loss.
                await _journal.SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
                snapshotId = snapshot.Id;

                step = step with { SnapshotId = snapshotId };
                await _journal.UpdateStepAsync(step, cancellationToken).ConfigureAwait(false);
            }

            ApplyResult apply = await tweak.ApplyAsync(context, cancellationToken).ConfigureAwait(false);

            if (apply.Outcome == ApplyOutcome.Failed)
            {
                return await FailStepAsync(
                    step, tweakId, compatibility, observation, apply.Message, accessor, cancellationToken)
                    .ConfigureAwait(false);
            }

            VerificationResult verification = apply.Outcome == ApplyOutcome.NoChangeRequired
                ? VerificationResult.Verified("No change was required.")
                : await tweak.VerifyAsync(context, cancellationToken).ConfigureAwait(false);

            if (!verification.IsAcceptable)
            {
                return await FailStepAsync(
                    step,
                    tweakId,
                    compatibility,
                    observation,
                    $"Verification failed: {verification.Message}",
                    accessor,
                    cancellationToken).ConfigureAwait(false);
            }

            WarnOnUnwrittenClaims(tweak, apply, accessor);

            step = step with
            {
                ApplyOutcome = apply.Outcome,
                VerificationStatus = verification.Status,
                Message = apply.Message,
                RollbackPayload = context.RollbackPayload,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
            };
            await _journal.UpdateStepAsync(step, cancellationToken).ConfigureAwait(false);

            bool changedSomething = accessor.WrittenKeys.Count > 0 || context.RollbackPayload is not null;

            if (changedSomething)
            {
                await _appliedTweaks.UpsertAsync(
                    new AppliedTweakRecord(
                        tweakId,
                        transactionId,
                        tweak.Descriptor.DefinitionVersion,
                        tweak.Descriptor.Scope,
                        _timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }

            await _audit.RecordAsync(
                AuditRecordFactory.Success(
                    ModuleName,
                    "apply",
                    tweakId,
                    transactionId,
                    observation.CurrentValueSummary,
                    observation.RecommendedValueSummary),
                cancellationToken).ConfigureAwait(false);

            return new StepExecution(
                new TweakRunResult
                {
                    TweakId = tweakId,
                    Compatibility = compatibility,
                    Outcome = apply.Outcome,
                    Verification = verification.Status,
                    Observation = observation,
                    Message = apply.Message,
                    ChangedKeys = accessor.WrittenKeys.Select(key => key.ToString()).ToList(),
                },
                Succeeded: true,
                Applied: changedSomething);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tweak {TweakId} threw during apply.", tweakId);

            // The cancellation token is deliberately not forwarded: the journal must be updated
            // even when the run is being torn down, or recovery has nothing to work from.
            return await FailStepAsync(
                step, tweakId, compatibility, observation, ex.Message, accessor, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<StepExecution> FailStepAsync(
        TransactionStep step,
        string tweakId,
        CompatibilityResult compatibility,
        TweakObservation? observation,
        string message,
        TransactionalStateAccessor accessor,
        CancellationToken cancellationToken)
    {
        step = step with
        {
            ApplyOutcome = ApplyOutcome.Failed,
            Message = message,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
        };
        await _journal.UpdateStepAsync(step, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditRecordFactory.Failure(ModuleName, "apply", message, tweakId, step.TransactionId),
            cancellationToken).ConfigureAwait(false);

        return new StepExecution(
            new TweakRunResult
            {
                TweakId = tweakId,
                Compatibility = compatibility,
                Outcome = ApplyOutcome.Failed,
                Observation = observation,
                Message = message,
                ChangedKeys = accessor.WrittenKeys.Select(key => key.ToString()).ToList(),
            },
            Succeeded: false,
            // A partial write still counts as applied: it is exactly the case rollback exists for.
            Applied: accessor.WrittenKeys.Count > 0);
    }

    private static async Task<StateSnapshot> CaptureSnapshotAsync(
        Guid transactionId,
        string tweakId,
        IReadOnlyList<StateKey> keys,
        TweakContext context,
        CancellationToken cancellationToken)
    {
        var entries = new List<SnapshotEntry>(keys.Count);

        foreach (StateKey key in keys)
        {
            StateValue value = await context.State.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            entries.Add(new SnapshotEntry(key, value));
        }

        return new StateSnapshot
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            TweakId = tweakId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }

    private async Task<CompatibilityResult> EvaluateCompatibilityAsync(
        ITweak tweak,
        TweakContext context,
        CancellationToken cancellationToken)
    {
        CompatibilityResult declared = CompatibilityEvaluator.Evaluate(
            tweak.Descriptor, context.Profile, context.CpuLayout, context.Privileges);

        if (!declared.IsSupported)
        {
            return declared;
        }

        try
        {
            return await tweak.CheckCompatibilityAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compatibility check for {TweakId} threw.", tweak.Descriptor.Id);
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.Unknown, $"Compatibility could not be determined: {ex.Message}");
        }
    }

    private void WarnOnUnwrittenClaims(ITweak tweak, ApplyResult apply, TransactionalStateAccessor accessor)
    {
        // The accessor already refuses undeclared writes. The remaining inconsistency worth
        // surfacing is a tweak that reports changing a key it never wrote, which usually means a
        // code path returned the wrong result object.
        var written = accessor.WrittenKeys.Select(key => key.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (StateKey claimed in apply.ChangedKeys)
        {
            if (!written.Contains(claimed.ToString()))
            {
                _logger.LogWarning(
                    "Tweak {TweakId} reported changing {Key} but no write was recorded for it.",
                    tweak.Descriptor.Id,
                    claimed.ToString());
            }
        }
    }

    private async Task<string> GetFingerprintAsync(CancellationToken cancellationToken)
    {
        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return profile.Fingerprint.CompositeHash;
    }
}
