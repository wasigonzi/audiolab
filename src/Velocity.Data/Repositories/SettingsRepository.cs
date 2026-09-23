using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Velocity.Data.Repositories;

/// <summary>Durable key/value settings for the application itself.</summary>
public interface ISettingsRepository
{
    /// <summary>Reads a setting.</summary>
    /// <param name="key">Setting key.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The value, or <see langword="null"/> when unset.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes a setting.</summary>
    /// <param name="key">Setting key.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="ISettingsRepository"/>.</summary>
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public SettingsRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = $key;";
        command.WithParameter("$key", key);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO setting (key, value, updated_at_utc)
            VALUES ($key, $value, $updated)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at_utc = excluded.updated_at_utc;
            """;
        command.WithParameter("$key", key)
               .WithParameter("$value", value)
               .WithParameter("$updated", SqliteValueConverter.ToText(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
