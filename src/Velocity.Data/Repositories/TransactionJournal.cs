using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Data.Repositories;

/// <summary>SQLite implementation of <see cref="ITransactionJournal"/>.</summary>
public sealed class TransactionJournal : ITransactionJournal
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the journal.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public TransactionJournal(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task CreateTransactionAsync(
        OptimizationTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO optimization_transaction
                (id, status, reason, profile_id, session_id, hardware_fingerprint,
                 started_at_utc, completed_at_utc)
            VALUES
                ($id, $status, $reason, $profile, $session, $fingerprint, $started, $completed);
            """;
        command.WithParameter("$id", SqliteValueConverter.ToText(transaction.Id))
               .WithParameter("$status", (int)transaction.Status)
               .WithParameter("$reason", (int)transaction.Reason)
               .WithParameter("$profile", transaction.ProfileId)
               .WithParameter("$session", SqliteValueConverter.ToTextOrNull(transaction.SessionId))
               .WithParameter("$fingerprint", transaction.HardwareFingerprint)
               .WithParameter("$started", SqliteValueConverter.ToText(transaction.StartedAtUtc))
               .WithParameter("$completed", SqliteValueConverter.ToTextOrNull(transaction.CompletedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateTransactionStatusAsync(
        Guid transactionId,
        TransactionStatus status,
        DateTimeOffset? completedAtUtc,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE optimization_transaction
               SET status = $status,
                   completed_at_utc = $completed
             WHERE id = $id;
            """;
        command.WithParameter("$status", (int)status)
               .WithParameter("$completed", SqliteValueConverter.ToTextOrNull(completedAtUtc))
               .WithParameter("$id", SqliteValueConverter.ToText(transactionId));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddStepAsync(TransactionStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO transaction_step
                (id, transaction_id, ordinal, tweak_id, tweak_version, snapshot_id,
                 apply_outcome, verification_status, message, rollback_payload,
                 started_at_utc, completed_at_utc, rolled_back)
            VALUES
                ($id, $transaction, $ordinal, $tweak, $version, $snapshot,
                 $outcome, $verification, $message, $payload,
                 $started, $completed, $rolledBack);
            """;
        BindStep(command, step);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateStepAsync(TransactionStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE transaction_step
               SET snapshot_id = $snapshot,
                   apply_outcome = $outcome,
                   verification_status = $verification,
                   message = $message,
                   rollback_payload = $payload,
                   completed_at_utc = $completed,
                   rolled_back = $rolledBack
             WHERE id = $id;
            """;
        BindStep(command, step);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkStepRolledBackAsync(Guid stepId, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE transaction_step SET rolled_back = 1 WHERE id = $id;";
        command.WithParameter("$id", SqliteValueConverter.ToText(stepId));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveSnapshotAsync(StateSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteTransaction dbTransaction = connection.BeginTransaction();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = dbTransaction;
            command.CommandText =
                """
                INSERT INTO state_snapshot (id, transaction_id, tweak_id, captured_at_utc)
                VALUES ($id, $transaction, $tweak, $captured);
                """;
            command.WithParameter("$id", SqliteValueConverter.ToText(snapshot.Id))
                   .WithParameter("$transaction", SqliteValueConverter.ToText(snapshot.TransactionId))
                   .WithParameter("$tweak", snapshot.TweakId)
                   .WithParameter("$captured", SqliteValueConverter.ToText(snapshot.CapturedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (SnapshotEntry entry in snapshot.Entries)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText =
                """
                INSERT INTO state_snapshot_entry
                    (snapshot_id, state_key, provider, path, item, value_kind, value_data)
                VALUES
                    ($snapshot, $key, $provider, $path, $item, $kind, $data);
                """;
            command.WithParameter("$snapshot", SqliteValueConverter.ToText(snapshot.Id))
                   .WithParameter("$key", entry.Key.ToString())
                   .WithParameter("$provider", entry.Key.Provider)
                   .WithParameter("$path", entry.Key.Path)
                   .WithParameter("$item", entry.Key.Item)
                   .WithParameter("$kind", (int)entry.Value.Kind)
                   .WithParameter("$data", entry.Value.Data);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StateSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();

        Guid transactionId;
        string tweakId;
        DateTimeOffset capturedAt;

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT transaction_id, tweak_id, captured_at_utc FROM state_snapshot WHERE id = $id;";
            command.WithParameter("$id", SqliteValueConverter.ToText(snapshotId));

            using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            transactionId = reader.GetGuid(0);
            tweakId = reader.GetString(1);
            capturedAt = reader.GetTimestamp(2);
        }

        var entries = new List<SnapshotEntry>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT provider, path, item, value_kind, value_data
                  FROM state_snapshot_entry
                 WHERE snapshot_id = $id
                 ORDER BY state_key;
                """;
            command.WithParameter("$id", SqliteValueConverter.ToText(snapshotId));

            using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = new StateKey(reader.GetString(0), reader.GetString(1), reader.GetNullableString(2));
                var value = new StateValue((StateValueKind)reader.GetInt32(3), reader.GetNullableString(4));
                entries.Add(new SnapshotEntry(key, value));
            }
        }

        return new StateSnapshot
        {
            Id = snapshotId,
            TransactionId = transactionId,
            TweakId = tweakId,
            CapturedAtUtc = capturedAt,
            Entries = entries,
        };
    }

    /// <inheritdoc />
    public async Task<OptimizationTransaction?> GetTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = TransactionSelect + " WHERE id = $id;";
        command.WithParameter("$id", SqliteValueConverter.ToText(transactionId));

        OptimizationTransaction? transaction = null;
        using (SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                transaction = ReadTransaction(reader);
            }
        }

        if (transaction is null)
        {
            return null;
        }

        IReadOnlyList<TransactionStep> steps =
            await ReadStepsAsync(connection, transactionId, cancellationToken).ConfigureAwait(false);
        return transaction with { Steps = steps };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OptimizationTransaction>> GetIncompleteTransactionsAsync(
        CancellationToken cancellationToken)
    {
        string statuses = string.Join(
            ',',
            (int)TransactionStatus.Pending,
            (int)TransactionStatus.Applying,
            (int)TransactionStatus.RollingBack);

        return QueryTransactionsAsync(
            $"{TransactionSelect} WHERE status IN ({statuses}) ORDER BY started_at_utc;",
            command: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OptimizationTransaction>> GetRecentTransactionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return QueryTransactionsAsync(
            $"{TransactionSelect} ORDER BY started_at_utc DESC LIMIT {limit.ToString(CultureInfo.InvariantCulture)};",
            command: null,
            cancellationToken);
    }

    private const string TransactionSelect =
        """
        SELECT id, status, reason, profile_id, session_id, hardware_fingerprint,
               started_at_utc, completed_at_utc
          FROM optimization_transaction
        """;

    private async Task<IReadOnlyList<OptimizationTransaction>> QueryTransactionsAsync(
        string sql,
        Action<SqliteCommand>? command,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        var transactions = new List<OptimizationTransaction>();

        using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = sql;
            command?.Invoke(query);

            using SqliteDataReader reader =
                await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                transactions.Add(ReadTransaction(reader));
            }
        }

        var result = new List<OptimizationTransaction>(transactions.Count);
        foreach (OptimizationTransaction transaction in transactions)
        {
            IReadOnlyList<TransactionStep> steps =
                await ReadStepsAsync(connection, transaction.Id, cancellationToken).ConfigureAwait(false);
            result.Add(transaction with { Steps = steps });
        }

        return result;
    }

    private static async Task<IReadOnlyList<TransactionStep>> ReadStepsAsync(
        SqliteConnection connection,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, transaction_id, ordinal, tweak_id, tweak_version, snapshot_id,
                   apply_outcome, verification_status, message, rollback_payload,
                   started_at_utc, completed_at_utc, rolled_back
              FROM transaction_step
             WHERE transaction_id = $id
             ORDER BY ordinal;
            """;
        command.WithParameter("$id", SqliteValueConverter.ToText(transactionId));

        var steps = new List<TransactionStep>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            steps.Add(new TransactionStep
            {
                Id = reader.GetGuid(0),
                TransactionId = reader.GetGuid(1),
                Ordinal = reader.GetInt32(2),
                TweakId = reader.GetString(3),
                TweakDefinitionVersion = reader.GetInt32(4),
                SnapshotId = reader.GetNullableGuid(5),
                ApplyOutcome = reader.IsDBNull(6) ? null : (ApplyOutcome)reader.GetInt32(6),
                VerificationStatus = reader.IsDBNull(7) ? null : (VerificationStatus)reader.GetInt32(7),
                Message = reader.GetNullableString(8),
                RollbackPayload = reader.GetNullableString(9),
                StartedAtUtc = reader.GetTimestamp(10),
                CompletedAtUtc = reader.GetNullableTimestamp(11),
                RolledBack = reader.GetInt32(12) != 0,
            });
        }

        return steps;
    }

    private static OptimizationTransaction ReadTransaction(IDataRecord record) => new()
    {
        Id = record.GetGuid(0),
        Status = (TransactionStatus)record.GetInt32(1),
        Reason = (TransactionReason)record.GetInt32(2),
        ProfileId = record.GetNullableString(3),
        SessionId = record.GetNullableGuid(4),
        HardwareFingerprint = record.GetString(5),
        StartedAtUtc = record.GetTimestamp(6),
        CompletedAtUtc = record.GetNullableTimestamp(7),
    };

    private static void BindStep(SqliteCommand command, TransactionStep step) =>
        command.WithParameter("$id", SqliteValueConverter.ToText(step.Id))
               .WithParameter("$transaction", SqliteValueConverter.ToText(step.TransactionId))
               .WithParameter("$ordinal", step.Ordinal)
               .WithParameter("$tweak", step.TweakId)
               .WithParameter("$version", step.TweakDefinitionVersion)
               .WithParameter("$snapshot", SqliteValueConverter.ToTextOrNull(step.SnapshotId))
               .WithParameter("$outcome", step.ApplyOutcome is null ? null : (int)step.ApplyOutcome.Value)
               .WithParameter(
                   "$verification",
                   step.VerificationStatus is null ? null : (int)step.VerificationStatus.Value)
               .WithParameter("$message", step.Message)
               .WithParameter("$payload", step.RollbackPayload)
               .WithParameter("$started", SqliteValueConverter.ToText(step.StartedAtUtc))
               .WithParameter("$completed", SqliteValueConverter.ToTextOrNull(step.CompletedAtUtc))
               .WithParameter("$rolledBack", step.RolledBack ? 1 : 0);
}
