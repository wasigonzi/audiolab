using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Profiles;

namespace Velocity.Data.Repositories;

/// <summary>Stores optimization profiles.</summary>
public interface IProfileRepository
{
    /// <summary>Creates or replaces a profile.</summary>
    /// <param name="profile">Profile to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task UpsertAsync(OptimizationProfile profile, CancellationToken cancellationToken);

    /// <summary>Reads a profile by identifier.</summary>
    /// <param name="id">Profile identifier.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profile, or <see langword="null"/> when it does not exist.</returns>
    Task<OptimizationProfile?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Reads every profile, ordered by name.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profiles.</returns>
    Task<IReadOnlyList<OptimizationProfile>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Deletes a profile.</summary>
    /// <param name="id">Profile identifier.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="IProfileRepository"/>.</summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public ProfileRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(OptimizationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO optimization_profile
                (id, name, kind, game_id, is_built_in, schema_version,
                 created_at_utc, modified_at_utc, document)
            VALUES
                ($id, $name, $kind, $game, $builtIn, $schema, $created, $modified, $document)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name,
                kind = excluded.kind,
                game_id = excluded.game_id,
                is_built_in = excluded.is_built_in,
                schema_version = excluded.schema_version,
                modified_at_utc = excluded.modified_at_utc,
                document = excluded.document;
            """;
        command.WithParameter("$id", profile.Id)
               .WithParameter("$name", profile.Name)
               .WithParameter("$kind", (int)profile.Kind)
               .WithParameter("$game", profile.GameId)
               .WithParameter("$builtIn", profile.IsBuiltIn ? 1 : 0)
               .WithParameter("$schema", profile.SchemaVersion)
               .WithParameter("$created", SqliteValueConverter.ToText(profile.CreatedAtUtc))
               .WithParameter("$modified", SqliteValueConverter.ToText(profile.ModifiedAtUtc))
               .WithParameter("$document", JsonSerializer.Serialize(profile, VelocityJson.Options));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OptimizationProfile?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM optimization_profile WHERE id = $id;";
        command.WithParameter("$id", id);

        object? document = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return document is string json
            ? JsonSerializer.Deserialize<OptimizationProfile>(json, VelocityJson.Options)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OptimizationProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM optimization_profile ORDER BY name;";

        var profiles = new List<OptimizationProfile>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            OptimizationProfile? profile =
                JsonSerializer.Deserialize<OptimizationProfile>(reader.GetString(0), VelocityJson.Options);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM optimization_profile WHERE id = $id AND is_built_in = 0;";
        command.WithParameter("$id", id);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }
}
