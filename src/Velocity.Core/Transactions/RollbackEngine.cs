using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Auditing;
using Velocity.Core.Tweaks;
using Velocity.Data.Repositories;

namespace Velocity.Core.Transactions;

/// <summary>Outcome of a rollback attempt.</summary>
/// <param name="TransactionId">Transaction that was rolled back.</param>
/// <param name="RestoredKeyCount">Number of state keys written back.</param>
/// <param name="Failures">Keys that could not be restored, with the reason.</param>
public sealed record RollbackResult(
    Guid TransactionId,
    int RestoredKeyCount,
    IReadOnlyDictionary<string, string> Failures)
{
    /// <summary><see langword="true"/> when every captured value was written back.</summary>
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>Reverses changes recorded in the transaction journal.</summary>
public interface IRollbackEngine
{
    /// <summary>Reverses every step of a transaction, most recent step first.</summary>
    /// <param name="transactionId">Transaction to reverse.</param>
    /// <param name="cancellationToken">Token used to abort the rollback.</param>
    /// <returns>What was restored and what failed.</returns>
    Task<RollbackResult> RollbackTransactionAsync(Guid transactionId, CancellationToken cancellationToken);

    /// <summary>Reverses the most recent applied transaction.</summary>
    /// <param name="cancellationToken">Token used to abort the rollback.</param>
    /// <returns>
    /// The rollback result, or <see langword="null"/> when nothing this product applied is
    /// outstanding.
    /// </returns>
    Task<RollbackResult?> RollbackLastAsync(CancellationToken cancellationToken);

    /// <summary>Reverses one tweak, using the snapshot from the transaction that applied it.</summary>
    /// <param name="tweakId">Tweak to reverse.</param>
    /// <param name="cancellationToken">Token used to abort the rollback.</param>
    /// <returns>
    /// The rollback result, or <see langword="null"/> when this product has not applied that tweak.
    /// </returns>
    Task<RollbackResult?> RollbackTweakAsync(string tweakId, CancellationToken cancellationToken);
}

/// <summary>
/// The generic rollback engine.
/// </summary>
/// <remarks>
/// <para>
/// Rollback writes captured values back through the same state providers that applied them. It is
/// deliberately generic: a tweak does not get to implement its own undo path, because an undo path
/// written by hand drifts from the apply path and is only exercised when something has already
/// gone wrong. Tweaks whose effect is not expressible as state (a suspended process, for example)
/// implement <see cref="ICustomRollback"/>, which runs first.
/// </para>
/// <para>
/// Restoring a value captured as <see cref="StateValueKind.Absent"/> deletes the item rather than
/// writing a default, so a setting the user never had does not end up existing afterwards.
/// </para>
/// <para>
/// Rollback is idempotent. Re-running it over an already restored step writes the same values
/// again, which matters because crash recovery cannot know how far a previous attempt got.
/// </para>
/// </remarks>
public sealed class RollbackEngine : IRollbackEngine
{
    private const string ModuleName = "Core.RollbackEngine";

    private readonly ITransactionJournal _journal;
    private readonly ITweakRegistry _registry;
    private readonly ITweakContextFactory _contextFactory;
    private readonly IAppliedTweakRepository _appliedTweaks;
    private readonly IAuditSink _audit;
    private readonly ILogger<RollbackEngine> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the engine.</summary>
    /// <param name="journal">Transaction journal.</param>
    /// <param name="registry">Tweak catalogue, used to find custom rollback implementations.</param>
    /// <param name="contextFactory">Factory for tweak execution contexts.</param>
    /// <param name="appliedTweaks">Record of what is currently applied.</param>
    /// <param name="audit">Audit sink.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injected so tests can control timestamps.</param>
    public RollbackEngine(
        ITransactionJournal journal,
        ITweakRegistry registry,
        ITweakContextFactory contextFactory,
        IAppliedTweakRepository appliedTweaks,
        IAuditSink audit,
        ILogger<RollbackEngine> logger,
        TimeProvider? timeProvider = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _appliedTweaks = appliedTweaks ?? throw new ArgumentNullException(nameof(appliedTweaks));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<RollbackResult> RollbackTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        OptimizationTransaction? transaction =
            await _journal.GetTransactionAsync(transactionId, cancellationToken).ConfigureAwait(false);

        if (transaction is null)
        {
            throw new InvalidOperationException($"Transaction {transactionId} is not in the journal.");
        }

        await _journal.UpdateTransactionStatusAsync(
            transactionId, TransactionStatus.RollingBack, null, cancellationToken).ConfigureAwait(false);

        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        int restored = 0;

        // Reverse order: a later step may depend on state an earlier step captured.
        foreach (TransactionStep step in transaction.Steps.OrderByDescending(step => step.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (step.RolledBack)
            {
                continue;
            }

            restored += await RollbackStepAsync(step, failures, cancellationToken).ConfigureAwait(false);
        }

        TransactionStatus finalStatus = failures.Count == 0
            ? TransactionStatus.RolledBack
            : TransactionStatus.RollbackFailed;

        await _journal.UpdateTransactionStatusAsync(
            transactionId, finalStatus, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            failures.Count == 0
                ? AuditRecordFactory.Success(
                    ModuleName,
                    "rollback",
                    transactionId.ToString(),
                    transactionId,
                    newValue: string.Create(CultureInfo.InvariantCulture, $"{restored} key(s) restored"))
                : AuditRecordFactory.Failure(
                    ModuleName,
                    "rollback",
                    string.Join("; ", failures.Select(failure => $"{failure.Key}: {failure.Value}")),
                    transactionId.ToString(),
                    transactionId),
            cancellationToken).ConfigureAwait(false);

        return new RollbackResult(transactionId, restored, failures);
    }

    /// <inheritdoc />
    public async Task<RollbackResult?> RollbackLastAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OptimizationTransaction> recent =
            await _journal.GetRecentTransactionsAsync(20, cancellationToken).ConfigureAwait(false);

        OptimizationTransaction? target = recent.FirstOrDefault(
            transaction => transaction.Status is TransactionStatus.Applied
                or TransactionStatus.RollbackFailed);

        return target is null
            ? null
            : await RollbackTransactionAsync(target.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RollbackResult?> RollbackTweakAsync(string tweakId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);

        IReadOnlyList<AppliedTweakRecord> applied =
            await _appliedTweaks.GetAllAsync(cancellationToken).ConfigureAwait(false);

        AppliedTweakRecord? record = applied
            .Where(entry => string.Equals(entry.TweakId, tweakId, StringComparison.OrdinalIgnoreCase))
            .Cast<AppliedTweakRecord?>()
            .FirstOrDefault();

        if (record is null)
        {
            return null;
        }

        OptimizationTransaction? transaction = await _journal
            .GetTransactionAsync(record.Value.TransactionId, cancellationToken)
            .ConfigureAwait(false);

        TransactionStep? step = transaction?.Steps.FirstOrDefault(
            candidate => string.Equals(candidate.TweakId, tweakId, StringComparison.OrdinalIgnoreCase) &&
                         !candidate.RolledBack);

        if (transaction is null || step is null)
        {
            return null;
        }

        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        int restored = await RollbackStepAsync(step, failures, cancellationToken).ConfigureAwait(false);

        bool everyStepRolledBack = (await _journal
            .GetTransactionAsync(transaction.Id, cancellationToken)
            .ConfigureAwait(false))
            ?.Steps.All(candidate => candidate.RolledBack || candidate.ApplyOutcome == ApplyOutcome.Skipped)
            ?? false;

        if (everyStepRolledBack)
        {
            await _journal.UpdateTransactionStatusAsync(
                transaction.Id,
                failures.Count == 0 ? TransactionStatus.RolledBack : TransactionStatus.RollbackFailed,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        return new RollbackResult(transaction.Id, restored, failures);
    }

    private async Task<int> RollbackStepAsync(
        TransactionStep step,
        Dictionary<string, string> failures,
        CancellationToken cancellationToken)
    {
        int restored = 0;

        StateSnapshot? snapshot = step.SnapshotId is Guid snapshotId
            ? await _journal.GetSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false)
            : null;

        IReadOnlyList<SnapshotEntry> entries = snapshot?.Entries ?? Array.Empty<SnapshotEntry>();

        try
        {
            await RunCustomRollbackAsync(step, entries, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Custom rollback for tweak {TweakId} failed.", step.TweakId);
            failures[$"{step.TweakId}:custom"] = ex.Message;
        }

        if (entries.Count > 0)
        {
            (TweakContext context, _) = await _contextFactory
                .CreateForKeysAsync(step.TweakId, entries.Select(entry => entry.Key), cancellationToken)
                .ConfigureAwait(false);

            foreach (SnapshotEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await context.State.WriteAsync(entry.Key, entry.Value, cancellationToken)
                        .ConfigureAwait(false);
                    restored++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex, "Failed to restore {Key} for tweak {TweakId}.", entry.Key.ToString(), step.TweakId);
                    failures[entry.Key.ToString()] = ex.Message;
                }
            }
        }

        if (failures.Count == 0)
        {
            await _journal.MarkStepRolledBackAsync(step.Id, cancellationToken).ConfigureAwait(false);
            await _appliedTweaks.RemoveAsync(step.TweakId, cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    private async Task RunCustomRollbackAsync(
        TransactionStep step,
        IReadOnlyList<SnapshotEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_registry.Find(step.TweakId) is not ICustomRollback custom)
        {
            return;
        }

        (TweakContext context, _) = await _contextFactory
            .CreateForKeysAsync(step.TweakId, entries.Select(entry => entry.Key), cancellationToken)
            .ConfigureAwait(false);

        await custom.RollbackAsync(context, step.RollbackPayload, cancellationToken).ConfigureAwait(false);
    }
}
