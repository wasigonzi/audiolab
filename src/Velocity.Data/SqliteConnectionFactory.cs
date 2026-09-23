using System;
using System.Data;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Hosting;

namespace Velocity.Data;

/// <summary>Creates open connections to the Velocity database.</summary>
public interface IDatabaseConnectionFactory : IDisposable
{
    /// <summary>Opens a new connection with the product's pragmas applied.</summary>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    SqliteConnection CreateOpenConnection();
}

/// <summary>
/// Default <see cref="IDatabaseConnectionFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Write-ahead logging is enabled because two processes touch this database: the desktop UI and
/// the privileged helper. WAL plus a busy timeout is what keeps a helper write from failing while
/// the UI is reading the journal.
/// </para>
/// <para>
/// <c>foreign_keys</c> is a per connection pragma in SQLite, not a database property, so it has to
/// be set on every connection or the cascade deletes that keep snapshots tied to their transaction
/// silently do nothing.
/// </para>
/// </remarks>
public sealed class SqliteConnectionFactory : IDatabaseConnectionFactory
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;
    private bool _disposed;

    /// <summary>Creates a factory for the database file described by <paramref name="paths"/>.</summary>
    /// <param name="paths">Product filesystem layout.</param>
    public SqliteConnectionFactory(IVelocityPaths paths)
        : this(BuildFileConnectionString(paths))
    {
    }

    /// <summary>Creates a factory for an explicit connection string.</summary>
    /// <param name="connectionString">SQLite connection string.</param>
    public SqliteConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;

        // A shared-cache in-memory database only exists while at least one connection is open.
        if (connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
    }

    /// <summary>
    /// Creates a factory backed by a uniquely named shared in-memory database. Used by tests so
    /// that each test gets an isolated database with the real schema and no filesystem access.
    /// </summary>
    /// <returns>The factory.</returns>
    public static SqliteConnectionFactory CreateInMemory() =>
        new($"Data Source=velocity-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True");

    /// <inheritdoc />
    public SqliteConnection CreateOpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        if (_keepAlive is null)
        {
            using SqliteCommand walPragma = connection.CreateCommand();
            walPragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
            walPragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static string BuildFileConnectionString(IVelocityPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();

        return new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }
}

/// <summary>Convenience helpers for command construction.</summary>
internal static class SqliteCommandExtensions
{
    internal static SqliteCommand WithParameter(this SqliteCommand command, string name, object? value)
    {
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return command;
    }

    internal static string? GetNullableString(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : record.GetString(ordinal);

    internal static Guid? GetNullableGuid(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : Guid.Parse(record.GetString(ordinal));

    internal static int? GetNullableInt32(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : record.GetInt32(ordinal);

    internal static double? GetNullableDouble(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : record.GetDouble(ordinal);

    internal static long? GetNullableInt64(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : record.GetInt64(ordinal);
}
