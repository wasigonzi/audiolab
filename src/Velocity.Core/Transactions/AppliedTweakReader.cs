using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Transactions;
using Velocity.Data.Repositories;

namespace Velocity.Core.Transactions;

/// <summary>
/// Reads what this product has applied, and the value it captured beforehand.
/// </summary>
/// <remarks>
/// The distinction between "this machine happens to be configured that way" and "we configured it
/// that way" is a product concept, not a storage detail: it decides whether the Restore Center
/// offers to put something back, and whether Expert Mode can show an original value. This is the
/// query that answers it.
/// </remarks>
public interface IAppliedTweakReader
{
    /// <summary>
    /// Returns, for each tweak this product currently has applied, the value captured before it
    /// was applied, rendered for display.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>Original values keyed by tweak id. A null value means the item did not exist.</returns>
    Task<IReadOnlyDictionary<string, string?>> GetAppliedOriginalValuesAsync(
        CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IAppliedTweakReader"/>, backed by the journal.</summary>
public sealed class AppliedTweakReader : IAppliedTweakReader
{
    private readonly IAppliedTweakRepository _appliedTweaks;
    private readonly ITransactionJournal _journal;

    /// <summary>Creates the reader.</summary>
    /// <param name="appliedTweaks">Record of what is currently applied.</param>
    /// <param name="journal">Transaction journal holding the captured values.</param>
    public AppliedTweakReader(IAppliedTweakRepository appliedTweaks, ITransactionJournal journal)
    {
        _appliedTweaks = appliedTweaks ?? throw new ArgumentNullException(nameof(appliedTweaks));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> GetAppliedOriginalValuesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AppliedTweakRecord> applied =
            await _appliedTweaks.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (AppliedTweakRecord record in applied)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OptimizationTransaction? transaction = await _journal
                .GetTransactionAsync(record.TransactionId, cancellationToken)
                .ConfigureAwait(false);

            TransactionStep? step = transaction?.Steps
                .LastOrDefault(candidate => string.Equals(
                    candidate.TweakId, record.TweakId, StringComparison.OrdinalIgnoreCase));

            string? original = null;

            if (step?.SnapshotId is Guid snapshotId)
            {
                StateSnapshot? snapshot =
                    await _journal.GetSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);

                // The first captured entry is the representative one for display; Expert Mode shows
                // the full key list separately.
                if (snapshot is { Entries.Count: > 0 })
                {
                    original = snapshot.Entries[0].Value.ToDisplayString();
                }
            }

            result[record.TweakId] = original;
        }

        return result;
    }
}
