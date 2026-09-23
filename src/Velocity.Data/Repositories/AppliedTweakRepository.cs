using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Data.Repositories;

/// <summary>A tweak this product has applied and not yet rolled back.</summary>
/// <param name="TweakId">Tweak identifier.</param>
/// <param name="TransactionId">Transaction that applied it.</param>
/// <param name="DefinitionVersion">Definition version at apply time.</param>
/// <param name="Scope">Whether the change is session scoped or persistent.</param>
/// <param name="AppliedAtUtc">When it was applied.</param>
public readonly record struct AppliedTweakRecord(
    string TweakId,
    Guid TransactionId,
    int DefinitionVersion,
    TweakScope Scope,
    DateTimeOffset AppliedAtUtc);

/// <summary>
/// Tracks which tweaks are currently applied by this product.
/// </summary>
/// <remarks>
/// This table is what lets the UI distinguish "this machine happens to be configured that way"
/// from "we configured it that way", which is the difference between an honest restore and
/// overwriting the user's own settings with a guessed default.
/// </remarks>
public interface IAppliedTweakRepository
{
    /// <summary>Records or replaces the applied state of a tweak.</summary>
    /// <param name="record">Applied tweak record.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task UpsertAsync(AppliedTweakRecord record, CancellationToken cancellationToken);

    /// <summary>Removes the applied state of a tweak after it has been rolled back.</summary>
    /// <param name="tweakId">Tweak identifier.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is removed.</returns>
    Task RemoveAsync(string tweakId, CancellationToken cancellationToken);

    /// <summary>Reads every currently applied tweak.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The applied tweaks.</returns>
    Task<IReadOnlyList<AppliedTweakRecord>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="IAppliedTweakRepository"/>.</summary>
public sealed class AppliedTweakRepository : IAppliedTweakRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public AppliedTweakRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(AppliedTweakRecord record, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO applied_tweak (tweak_id, transaction_id, definition_version, scope, applied_at_utc)
            VALUES ($tweak, $transaction, $version, $scope, $applied)
            ON CONFLICT (tweak_id) DO UPDATE SET
                transaction_id = excluded.transaction_id,
                definition_version = excluded.definition_version,
                scope = excluded.scope,
                applied_at_utc = excluded.applied_at_utc;
            """;
        command.WithParameter("$tweak", record.TweakId)
               .WithParameter("$transaction", SqliteValueConverter.ToText(record.TransactionId))
               .WithParameter("$version", record.DefinitionVersion)
               .WithParameter("$scope", (int)record.Scope)
               .WithParameter("$applied", SqliteValueConverter.ToText(record.AppliedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string tweakId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM applied_tweak WHERE tweak_id = $tweak;";
        command.WithParameter("$tweak", tweakId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppliedTweakRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT tweak_id, transaction_id, definition_version, scope, applied_at_utc
              FROM applied_tweak
             ORDER BY applied_at_utc;
            """;

        var records = new List<AppliedTweakRecord>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new AppliedTweakRecord(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                (TweakScope)reader.GetInt32(3),
                reader.GetTimestamp(4)));
        }

        return records;
    }
}
