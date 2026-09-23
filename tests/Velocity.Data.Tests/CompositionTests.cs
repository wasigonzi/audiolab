using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Abstractions.Hosting;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Velocity.Diagnostics;
using Velocity.Diagnostics.Logging;
using Xunit;

namespace Velocity.Data.Tests;

/// <summary>
/// Resolves the persistence layer the way a host does, against a real database file.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this suite constructs its collaborators by hand and runs against an
/// in-memory database. That is fast and isolated, and it missed a defect that made the product
/// unusable in every real deployment: registered by type, the container chose
/// <see cref="DatabaseMigrator"/>'s test constructor, because an unregistered
/// <see cref="IEnumerable{T}"/> resolves to an empty sequence rather than failing. The migrator
/// came up with no migrations, created nothing but <c>schema_version</c>, and the first query
/// after startup failed with "no such table".
/// </para>
/// <para>
/// So these tests go through the container and through the filesystem, which are the two things
/// the rest of the suite deliberately avoids.
/// </para>
/// </remarks>
public sealed class CompositionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "velocity-tests-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task TheResolvedMigratorCarriesEveryEmbeddedMigration()
    {
        await using ServiceProvider provider = BuildProvider();

        var migrator = (DatabaseMigrator)provider.GetRequiredService<IDatabaseMigrator>();

        Assert.True(
            migrator.TargetVersion >= 2,
            $"The resolved migrator knows about {migrator.TargetVersion} migrations. Registered by " +
            "type, the container picks the test constructor and this is zero.");
    }

    [Fact]
    public async Task StartupCreatesTheSchemaAndTheJournalIsImmediatelyReadable()
    {
        await using ServiceProvider provider = BuildProvider();

        // This is the startup sequence: migrate, then read the journal, exactly as the host does.
        int version = await provider.GetRequiredService<IDatabaseMigrator>()
            .MigrateAsync(CancellationToken.None);

        IReadOnlyList<Abstractions.Transactions.OptimizationTransaction> recent =
            await provider.GetRequiredService<ITransactionJournal>()
                .GetRecentTransactionsAsync(5, CancellationToken.None);

        Assert.True(version >= 2);
        Assert.Empty(recent);
    }

    [Fact]
    public async Task EveryTableTheProductQueriesExistsAfterStartup()
    {
        await using ServiceProvider provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseMigrator>().MigrateAsync(CancellationToken.None);

        var factory = provider.GetRequiredService<IDatabaseConnectionFactory>();
        using SqliteConnection connection = factory.CreateOpenConnection();

        foreach (string table in new[]
                 {
                     "schema_version",
                     "system_profile",
                     "optimization_transaction",
                     "state_snapshot",
                     "state_snapshot_entry",
                     "transaction_step",
                     "applied_tweak",
                     "optimization_profile",
                     "audit_log",
                     "benchmark_run",
                     "setting",
                     "tweak_trial_result",
                 })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);

            long found = Convert.ToInt64(
                await command.ExecuteScalarAsync(CancellationToken.None), provider: null);

            Assert.True(found == 1, $"The table '{table}' was not created by startup.");
        }
    }

    [Fact]
    public async Task TheDatabaseIsCreatedOnDiskWhereThePathsSayItShouldBe()
    {
        await using ServiceProvider provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseMigrator>().MigrateAsync(CancellationToken.None);

        var paths = provider.GetRequiredService<IVelocityPaths>();
        Assert.True(File.Exists(paths.DatabaseFile), $"No database at {paths.DatabaseFile}.");
    }

    [Fact]
    public async Task EveryRepositoryTheHostRegistersCanBeResolvedAndQueried()
    {
        // A repository that cannot be constructed is a startup crash, not a test failure later.
        await using ServiceProvider provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseMigrator>().MigrateAsync(CancellationToken.None);

        Assert.Empty(await provider.GetRequiredService<IProfileRepository>()
            .GetAllAsync(CancellationToken.None));
        Assert.Empty(await provider.GetRequiredService<IBenchmarkRepository>()
            .GetRunsAsync("fingerprint", "workload", 5, CancellationToken.None));
        Assert.Empty(await provider.GetRequiredService<ITrialResultRepository>()
            .GetResultsAsync("fingerprint", "workload", CancellationToken.None));
        Assert.Null(await provider.GetRequiredService<ISettingsRepository>()
            .GetAsync("missing", CancellationToken.None));
        Assert.Empty(await provider.GetRequiredService<IAppliedTweakRepository>()
            .GetAllAsync(CancellationToken.None));
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddVelocityDiagnostics(
            new VelocityPaths(_directory),
            options =>
            {
                options.WriteToConsole = false;
                options.FileNamePrefix = "velocity-tests";
            });

        services.AddVelocityData();
        return services.BuildServiceProvider();
    }
}
