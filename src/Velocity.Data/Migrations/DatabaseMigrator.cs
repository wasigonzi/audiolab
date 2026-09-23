using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Velocity.Data.Migrations;

/// <summary>One forward-only schema migration.</summary>
/// <param name="Version">Sequential version number.</param>
/// <param name="Description">Human readable description, stored in <c>schema_version</c>.</param>
/// <param name="Sql">The statements to execute.</param>
public sealed record SchemaMigration(int Version, string Description, string Sql);

/// <summary>Brings the database schema up to the version this build expects.</summary>
public interface IDatabaseMigrator
{
    /// <summary>Applies every migration the database has not yet seen.</summary>
    /// <param name="cancellationToken">Token used to abort the migration.</param>
    /// <returns>The schema version after migration.</returns>
    Task<int> MigrateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Applies embedded SQL migrations inside a transaction, one version at a time.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are embedded resources named <c>NNN_description.sql</c>. Ordering comes from the
/// numeric prefix rather than from resource enumeration order, which is not guaranteed.
/// </para>
/// <para>
/// Each migration and its <c>schema_version</c> row are committed together, so an interrupted
/// upgrade leaves the database at the last fully applied version rather than half way through one.
/// </para>
/// </remarks>
public sealed partial class DatabaseMigrator : IDatabaseMigrator
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    /// <summary>Creates a migrator that reads migrations embedded in this assembly.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="logger">Logger.</param>
    public DatabaseMigrator(IDatabaseConnectionFactory connectionFactory, ILogger<DatabaseMigrator> logger)
        : this(connectionFactory, logger, LoadEmbeddedMigrations())
    {
    }

    /// <summary>Creates a migrator with an explicit migration set, used by tests.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="migrations">Migrations to apply, in any order.</param>
    public DatabaseMigrator(
        IDatabaseConnectionFactory connectionFactory,
        ILogger<DatabaseMigrator> logger,
        IEnumerable<SchemaMigration> migrations)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _migrations = migrations?.OrderBy(m => m.Version).ToList()
            ?? throw new ArgumentNullException(nameof(migrations));
    }

    /// <summary>The highest schema version this build knows about.</summary>
    public int TargetVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <inheritdoc />
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);
        int current = await GetCurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (SchemaMigration migration in _migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (migration.Version <= current)
            {
                continue;
            }

            _logger.LogInformation(
                "Applying schema migration {Version}: {Description}",
                migration.Version,
                migration.Description);

            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (SqliteCommand record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_version (version, description, applied_at_utc) " +
                    "VALUES ($version, $description, $applied);";
                record.WithParameter("$version", migration.Version)
                      .WithParameter("$description", migration.Description)
                      .WithParameter("$applied", SqliteValueConverter.ToText(DateTimeOffset.UtcNow));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            current = migration.Version;
        }

        return current;
    }

    /// <summary>Reads the migrations embedded in this assembly.</summary>
    /// <returns>The migration set, ordered by version.</returns>
    public static IReadOnlyList<SchemaMigration> LoadEmbeddedMigrations()
    {
        Assembly assembly = typeof(DatabaseMigrator).Assembly;
        var migrations = new List<SchemaMigration>();

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = MigrationNamePattern().Match(resourceName);
            if (!match.Success)
            {
                continue;
            }

            int version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
            string description = match.Groups["description"].Value.Replace('_', ' ');

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            migrations.Add(new SchemaMigration(version, description, reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }

    /// <summary>
    /// Creates the bookkeeping table if it is missing. The migrator owns this table rather than
    /// letting the first migration create it, so that an alternative migration set (a test, or a
    /// future repair tool) does not have to reproduce it.
    /// </summary>
    private static async Task EnsureVersionTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version         INTEGER NOT NULL PRIMARY KEY,
                description     TEXT    NOT NULL,
                applied_at_utc  TEXT    NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetCurrentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        object? result = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(?<version>\d{3})_(?<description>[A-Za-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationNamePattern();
}
