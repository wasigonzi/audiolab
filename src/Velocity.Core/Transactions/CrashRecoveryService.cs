using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Transactions;
using Velocity.Core.Auditing;
using Velocity.Data.Repositories;

namespace Velocity.Core.Transactions;

/// <summary>Outcome of the startup recovery sweep.</summary>
/// <param name="RecoveredTransactionCount">Transactions that were rolled back.</param>
/// <param name="FailedTransactionIds">Transactions that could not be fully rolled back.</param>
public sealed record RecoveryReport(
    int RecoveredTransactionCount,
    IReadOnlyList<Guid> FailedTransactionIds)
{
    /// <summary><see langword="true"/> when nothing needed recovering.</summary>
    public bool NothingToDo => RecoveredTransactionCount == 0 && FailedTransactionIds.Count == 0;
}

/// <summary>Repairs the machine after the application was killed mid-transaction.</summary>
public interface ICrashRecoveryService
{
    /// <summary>
    /// Rolls back every transaction left in a non terminal state.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the sweep.</param>
    /// <returns>What was recovered.</returns>
    Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICrashRecoveryService"/>, run once during startup before the UI is shown.
/// </summary>
/// <remarks>
/// <para>
/// A transaction in <see cref="TransactionStatus.Pending"/>, <see cref="TransactionStatus.Applying"/>
/// or <see cref="TransactionStatus.RollingBack"/> means the process died while system state was in
/// an unknown condition. Recovery restores every captured value for those transactions.
/// </para>
/// <para>
/// Restoring a <c>Pending</c> transaction, where possibly nothing was written, is intentional and
/// safe: writing a captured value back over the identical current value is a no-op, and the
/// alternative (deciding how far the apply got) is not knowable.
/// </para>
/// </remarks>
public sealed class CrashRecoveryService : ICrashRecoveryService
{
    private const string ModuleName = "Core.CrashRecoveryService";

    private readonly ITransactionJournal _journal;
    private readonly IRollbackEngine _rollback;
    private readonly IAuditSink _audit;
    private readonly ILogger<CrashRecoveryService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="journal">Transaction journal.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="audit">Audit sink.</param>
    /// <param name="logger">Logger.</param>
    public CrashRecoveryService(
        ITransactionJournal journal,
        IRollbackEngine rollback,
        IAuditSink audit,
        ILogger<CrashRecoveryService> logger)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OptimizationTransaction> incomplete =
            await _journal.GetIncompleteTransactionsAsync(cancellationToken).ConfigureAwait(false);

        if (incomplete.Count == 0)
        {
            return new RecoveryReport(0, Array.Empty<Guid>());
        }

        _logger.LogWarning(
            "Found {Count} transaction(s) that did not complete. Restoring captured state before continuing.",
            incomplete.Count);

        var failed = new List<Guid>();
        int recovered = 0;

        foreach (OptimizationTransaction transaction in incomplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                RollbackResult result = await _rollback
                    .RollbackTransactionAsync(transaction.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    recovered++;
                }
                else
                {
                    failed.Add(transaction.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unrecoverable transaction must not stop the others from being recovered.
                _logger.LogError(ex, "Recovery of transaction {TransactionId} failed.", transaction.Id);
                failed.Add(transaction.Id);
            }
        }

        await _audit.RecordAsync(
            AuditRecordFactory.Success(
                ModuleName,
                "startup-recovery",
                newValue: string.Create(
                    CultureInfo.InvariantCulture,
                    $"recovered={recovered}, failed={failed.Count}")),
            cancellationToken).ConfigureAwait(false);

        return new RecoveryReport(recovered, failed);
    }
}
