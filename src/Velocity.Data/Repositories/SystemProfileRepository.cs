using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Hardware;

namespace Velocity.Data.Repositories;

/// <summary>Stores captured system profiles so results can be attributed to a machine state.</summary>
public interface ISystemProfileRepository
{
    /// <summary>Stores a captured profile.</summary>
    /// <param name="profile">Profile to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>Identifier of the stored row.</returns>
    Task<Guid> SaveAsync(SystemProfile profile, CancellationToken cancellationToken);

    /// <summary>Reads the most recently captured profile.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profile, or <see langword="null"/> when none has been captured.</returns>
    Task<SystemProfile?> GetLatestAsync(CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="ISystemProfileRepository"/>.</summary>
/// <remarks>
/// The whole profile is stored as a JSON document with the fingerprint and a few identifying
/// fields lifted into columns. Hardware models change shape often during development; promoting
/// every field to a column would mean a schema migration for each new probe.
/// </remarks>
public sealed class SystemProfileRepository : ISystemProfileRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public SystemProfileRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<Guid> SaveAsync(SystemProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var id = Guid.NewGuid();
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO system_profile
                (id, captured_at_utc, fingerprint_composite, fingerprint_cpu, fingerprint_gpu,
                 fingerprint_memory, fingerprint_os, os_build, cpu_brand, document)
            VALUES
                ($id, $captured, $composite, $cpu, $gpu, $memory, $os, $build, $brand, $document);
            """;
        command.WithParameter("$id", SqliteValueConverter.ToText(id))
               .WithParameter("$captured", SqliteValueConverter.ToText(profile.CapturedAtUtc))
               .WithParameter("$composite", profile.Fingerprint.CompositeHash)
               .WithParameter("$cpu", profile.Fingerprint.CpuHash)
               .WithParameter("$gpu", profile.Fingerprint.GpuHash)
               .WithParameter("$memory", profile.Fingerprint.MemoryHash)
               .WithParameter("$os", profile.Fingerprint.OsHash)
               .WithParameter("$build", profile.OperatingSystem.BuildNumber)
               .WithParameter("$brand", profile.Cpu.BrandString)
               .WithParameter("$document", JsonSerializer.Serialize(profile, VelocityJson.Options));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task<SystemProfile?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM system_profile ORDER BY captured_at_utc DESC LIMIT 1;";

        object? document = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return document is string json
            ? JsonSerializer.Deserialize<SystemProfile>(json, VelocityJson.Options)
            : null;
    }
}
