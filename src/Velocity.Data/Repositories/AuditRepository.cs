using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Diagnostics;

namespace Velocity.Data.Repositories;

/// <summary>Stores and queries the durable audit log.</summary>
public interface IAuditRepository
{
    /// <summary>Appends an audit record.</summary>
    /// <param name="record">Record to append.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task WriteAsync(OperationAuditRecord record, CancellationToken cancellationToken);

    /// <summary>Reads the most recent records, newest first.</summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The records.</returns>
    Task<IReadOnlyList<OperationAuditRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Reads every record belonging to one transaction, oldest first.</summary>
    /// <param name="transactionId">Transaction to filter by.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The records.</returns>
    Task<IReadOnlyList<OperationAuditRecord>> GetByTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="IAuditRepository"/>.</summary>
public sealed class AuditRepository : IAuditRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public AuditRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task WriteAsync(OperationAuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_log
                (id, timestamp_utc, module, action, target, original_value, new_value,
                 outcome, transaction_id, error, details)
            VALUES
                ($id, $timestamp, $module, $action, $target, $original, $new,
                 $outcome, $transaction, $error, $details);
            """;
        command.WithParameter("$id", SqliteValueConverter.ToText(record.Id))
               .WithParameter("$timestamp", SqliteValueConverter.ToText(record.TimestampUtc))
               .WithParameter("$module", record.Module)
               .WithParameter("$action", record.Action)
               .WithParameter("$target", record.Target)
               .WithParameter("$original", record.OriginalValue)
               .WithParameter("$new", record.NewValue)
               .WithParameter("$outcome", (int)record.Outcome)
               .WithParameter("$transaction", SqliteValueConverter.ToTextOrNull(record.TransactionId))
               .WithParameter("$error", record.Error)
               .WithParameter(
                   "$details",
                   record.Details.Count == 0
                       ? null
                       : JsonSerializer.Serialize(record.Details, VelocityJson.Options));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationAuditRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryAsync(
            $"{Select} ORDER BY timestamp_utc DESC LIMIT {limit.ToString(CultureInfo.InvariantCulture)};",
            bind: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationAuditRecord>> GetByTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"{Select} WHERE transaction_id = $transaction ORDER BY timestamp_utc;",
            command => command.WithParameter("$transaction", SqliteValueConverter.ToText(transactionId)),
            cancellationToken);

    private const string Select =
        """
        SELECT id, timestamp_utc, module, action, target, original_value, new_value,
               outcome, transaction_id, error, details
          FROM audit_log
        """;

    private async Task<IReadOnlyList<OperationAuditRecord>> QueryAsync(
        string sql,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);

        var records = new List<OperationAuditRecord>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string? details = reader.GetNullableString(10);
            records.Add(new OperationAuditRecord
            {
                Id = reader.GetGuid(0),
                TimestampUtc = reader.GetTimestamp(1),
                Module = reader.GetString(2),
                Action = reader.GetString(3),
                Target = reader.GetNullableString(4),
                OriginalValue = reader.GetNullableString(5),
                NewValue = reader.GetNullableString(6),
                Outcome = (AuditOutcome)reader.GetInt32(7),
                TransactionId = reader.GetNullableGuid(8),
                Error = reader.GetNullableString(9),
                Details = details is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(details, VelocityJson.Options)
                      ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
        }

        return records;
    }
}
