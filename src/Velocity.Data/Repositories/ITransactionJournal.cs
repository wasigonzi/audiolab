using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Transactions;

namespace Velocity.Data.Repositories;

/// <summary>
/// Durable record of every change the product makes.
/// </summary>
/// <remarks>
/// The journal is the mechanism behind two product promises: that a crash mid-apply is recoverable,
/// and that the Restore Center can tell the difference between a setting this product changed and
/// a setting the user already had. Both depend on writes reaching disk before the corresponding
/// system change, which is why every method here commits before returning.
/// </remarks>
public interface ITransactionJournal
{
    /// <summary>Writes a new transaction row.</summary>
    /// <param name="transaction">Transaction to record. Steps are written separately.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task CreateTransactionAsync(OptimizationTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Moves a transaction to a new lifecycle state.</summary>
    /// <param name="transactionId">Transaction to update.</param>
    /// <param name="status">New status.</param>
    /// <param name="completedAtUtc">Completion timestamp for terminal states.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task UpdateTransactionStatusAsync(
        Guid transactionId,
        TransactionStatus status,
        DateTimeOffset? completedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Writes a step row.</summary>
    /// <param name="step">Step to record.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task AddStepAsync(TransactionStep step, CancellationToken cancellationToken);

    /// <summary>Updates a step row with its outcome.</summary>
    /// <param name="step">Step with updated fields.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task UpdateStepAsync(TransactionStep step, CancellationToken cancellationToken);

    /// <summary>Records that a step's captured state has been written back.</summary>
    /// <param name="stepId">Step that was rolled back.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task MarkStepRolledBackAsync(Guid stepId, CancellationToken cancellationToken);

    /// <summary>Writes a snapshot and its entries in one database transaction.</summary>
    /// <param name="snapshot">Snapshot to record.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    Task SaveSnapshotAsync(StateSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Reads a snapshot with its entries.</summary>
    /// <param name="snapshotId">Snapshot to read.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The snapshot, or <see langword="null"/> when it does not exist.</returns>
    Task<StateSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>Reads a transaction with its steps in ordinal order.</summary>
    /// <param name="transactionId">Transaction to read.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The transaction, or <see langword="null"/> when it does not exist.</returns>
    Task<OptimizationTransaction?> GetTransactionAsync(Guid transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every transaction left in a non terminal state, oldest first. These are the
    /// transactions the recovery service rolls back at startup.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>Incomplete transactions with their steps.</returns>
    Task<IReadOnlyList<OptimizationTransaction>> GetIncompleteTransactionsAsync(CancellationToken cancellationToken);

    /// <summary>Reads recent transactions, newest first, for the Restore Center.</summary>
    /// <param name="limit">Maximum number of transactions to return.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>Recent transactions with their steps.</returns>
    Task<IReadOnlyList<OptimizationTransaction>> GetRecentTransactionsAsync(
        int limit,
        CancellationToken cancellationToken);
}
