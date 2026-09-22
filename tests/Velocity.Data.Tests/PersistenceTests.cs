using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Diagnostics;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Velocity.TestSupport;

namespace Velocity.Data.Tests;

/// <summary>Shared in-memory database with the real schema applied.</summary>
public abstract class DatabaseTestBase : IAsyncLifetime
{
    private SqliteConnectionFactory _factory = null!;

    protected IDatabaseConnectionFactory Factory => _factory;

    public async Task InitializeAsync()
    {
        _factory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(_factory, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Tests for the schema migrator.</summary>
public sealed class DatabaseMigratorTests
{
    [Fact]
    public async Task Migrate_CreatesTheSchemaAndRecordsTheVersion()
    {
        using SqliteConnectionFactory factory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(factory, NullLogger<DatabaseMigrator>.Instance);

        int version = await migrator.MigrateAsync(CancellationToken.None);

        Assert.Equal(migrator.TargetVersion, version);
        Assert.True(version >= 1);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        using SqliteConnectionFactory factory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(factory, NullLogger<DatabaseMigrator>.Instance);

        int first = await migrator.MigrateAsync(CancellationToken.None);
        int second = await migrator.MigrateAsync(CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Migrate_LeavesTheDatabaseAtTheLastGoodVersionWhenAMigrationFails()
    {
        using SqliteConnectionFactory factory = SqliteConnectionFactory.CreateInMemory();

        var migrations = new List<SchemaMigration>
        {
            new(1, "good", "CREATE TABLE good (id INTEGER PRIMARY KEY);"),
            new(2, "broken", "CREATE TABLE this is not valid sql;"),
        };

        var migrator = new DatabaseMigrator(factory, NullLogger<DatabaseMigrator>.Instance, migrations);

        await Assert.ThrowsAnyAsync<SqliteException>(() => migrator.MigrateAsync(CancellationToken.None));

        // The first migration and its version row committed together; the second rolled back whole.
        var reader = new DatabaseMigrator(
            factory,
            NullLogger<DatabaseMigrator>.Instance,
            new[] { migrations[0] });

        Assert.Equal(1, await reader.MigrateAsync(CancellationToken.None));
    }

    [Fact]
    public void EmbeddedMigrations_AreOrderedByVersion()
    {
        IReadOnlyList<SchemaMigration> migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        Assert.NotEmpty(migrations);
        Assert.Equal(migrations.OrderBy(migration => migration.Version), migrations);
    }
}

/// <summary>Tests for the transaction journal, which every safety guarantee rests on.</summary>
public sealed class TransactionJournalTests : DatabaseTestBase
{
    [Fact]
    public async Task Transaction_RoundTripsWithItsSteps()
    {
        var journal = new TransactionJournal(Factory);
        var transactionId = Guid.NewGuid();

        await journal.CreateTransactionAsync(Transaction(transactionId), CancellationToken.None);
        await journal.AddStepAsync(Step(transactionId, 0, "tweak.one"), CancellationToken.None);
        await journal.AddStepAsync(Step(transactionId, 1, "tweak.two"), CancellationToken.None);

        OptimizationTransaction? loaded =
            await journal.GetTransactionAsync(transactionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Steps.Count);
        Assert.Equal("tweak.one", loaded.Steps[0].TweakId);
        Assert.Equal(1, loaded.Steps[1].Ordinal);
    }

    [Fact]
    public async Task Snapshot_RoundTripsEveryValueKind()
    {
        var journal = new TransactionJournal(Factory);
        var transactionId = Guid.NewGuid();
        await journal.CreateTransactionAsync(Transaction(transactionId), CancellationToken.None);

        var snapshot = new StateSnapshot
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            TweakId = "tweak.one",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Entries = new[]
            {
                new SnapshotEntry(new StateKey("registry", @"HKLM\A", "Number"), StateValue.FromUInt32(7)),
                new SnapshotEntry(new StateKey("registry", @"HKLM\A", "Text"), StateValue.FromString("hello")),
                new SnapshotEntry(new StateKey("registry", @"HKLM\A", "Missing"), StateValue.Absent),
                new SnapshotEntry(
                    new StateKey("registry", @"HKLM\A", "Blob"),
                    new StateValue(StateValueKind.Binary, "AQIDBA==")),
            },
        };

        await journal.SaveSnapshotAsync(snapshot, CancellationToken.None);
        StateSnapshot? loaded = await journal.GetSnapshotAsync(snapshot.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Entries.Count);
        Assert.Contains(loaded.Entries, entry => entry.Value.IsAbsent);
        Assert.Contains(loaded.Entries, entry => entry.Value.Kind == StateValueKind.Binary);
    }

    [Fact]
    public async Task IncompleteTransactions_AreExactlyTheOnesRecoveryMustHandle()
    {
        var journal = new TransactionJournal(Factory);

        Guid pending = await AddTransactionAsync(journal, TransactionStatus.Pending);
        Guid applying = await AddTransactionAsync(journal, TransactionStatus.Applying);
        Guid rollingBack = await AddTransactionAsync(journal, TransactionStatus.RollingBack);
        await AddTransactionAsync(journal, TransactionStatus.Applied);
        await AddTransactionAsync(journal, TransactionStatus.RolledBack);

        IReadOnlyList<OptimizationTransaction> incomplete =
            await journal.GetIncompleteTransactionsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { pending, applying, rollingBack }.OrderBy(id => id),
            incomplete.Select(transaction => transaction.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task StatusUpdate_IsVisibleImmediately()
    {
        var journal = new TransactionJournal(Factory);
        var transactionId = Guid.NewGuid();
        await journal.CreateTransactionAsync(Transaction(transactionId), CancellationToken.None);

        await journal.UpdateTransactionStatusAsync(
            transactionId, TransactionStatus.Applied, DateTimeOffset.UtcNow, CancellationToken.None);

        OptimizationTransaction? loaded =
            await journal.GetTransactionAsync(transactionId, CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, loaded!.Status);
        Assert.NotNull(loaded.CompletedAtUtc);
    }

    [Fact]
    public async Task DeletingATransaction_CascadesToItsSnapshots()
    {
        var journal = new TransactionJournal(Factory);
        var transactionId = Guid.NewGuid();
        await journal.CreateTransactionAsync(Transaction(transactionId), CancellationToken.None);

        var snapshotId = Guid.NewGuid();
        await journal.SaveSnapshotAsync(
            new StateSnapshot
            {
                Id = snapshotId,
                TransactionId = transactionId,
                TweakId = "tweak.one",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Entries = new[] { new SnapshotEntry(new StateKey("registry", @"HKLM\A", "V"), StateValue.Absent) },
            },
            CancellationToken.None);

        using (SqliteConnection connection = Factory.CreateOpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            // Foreign keys are a per connection pragma in SQLite; this asserts the factory sets it.
            command.CommandText = "DELETE FROM optimization_transaction WHERE id = $id;";
            command.Parameters.AddWithValue("$id", transactionId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await journal.GetSnapshotAsync(snapshotId, CancellationToken.None));
    }

    [Fact]
    public async Task RecentTransactions_AreNewestFirst()
    {
        var journal = new TransactionJournal(Factory);

        for (int i = 0; i < 3; i++)
        {
            await journal.CreateTransactionAsync(
                Transaction(Guid.NewGuid()) with
                {
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                },
                CancellationToken.None);
        }

        IReadOnlyList<OptimizationTransaction> recent =
            await journal.GetRecentTransactionsAsync(10, CancellationToken.None);

        Assert.Equal(3, recent.Count);
        Assert.True(recent[0].StartedAtUtc >= recent[1].StartedAtUtc);
    }

    private static async Task<Guid> AddTransactionAsync(ITransactionJournal journal, TransactionStatus status)
    {
        var id = Guid.NewGuid();
        await journal.CreateTransactionAsync(Transaction(id) with { Status = status }, CancellationToken.None);
        return id;
    }

    private static OptimizationTransaction Transaction(Guid id) => new()
    {
        Id = id,
        Status = TransactionStatus.Pending,
        Reason = TransactionReason.ManualTweak,
        HardwareFingerprint = "fingerprint",
        StartedAtUtc = DateTimeOffset.UtcNow,
    };

    private static TransactionStep Step(Guid transactionId, int ordinal, string tweakId) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Ordinal = ordinal,
        TweakId = tweakId,
        TweakDefinitionVersion = 1,
        StartedAtUtc = DateTimeOffset.UtcNow,
    };
}

/// <summary>Tests for the remaining repositories.</summary>
public sealed class RepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task SystemProfile_RoundTripsThroughJson()
    {
        var repository = new SystemProfileRepository(Factory);
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.AmdDualChipletAsymmetricCache());

        await repository.SaveAsync(profile, CancellationToken.None);
        SystemProfile? loaded = await repository.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(profile.Cpu.BrandString, loaded!.Cpu.BrandString);
        Assert.Equal(profile.Cpu.PhysicalCoreCount, loaded.Cpu.PhysicalCoreCount);
        Assert.Equal(profile.Fingerprint.CompositeHash, loaded.Fingerprint.CompositeHash);
        Assert.Equal(profile.Cpu.Caches.Count, loaded.Cpu.Caches.Count);
    }

    [Fact]
    public async Task Profile_UpsertReplacesTheDocument()
    {
        var repository = new ProfileRepository(Factory);
        OptimizationProfile profile = Profile("competitive", ProfileKind.Competitive);

        await repository.UpsertAsync(profile, CancellationToken.None);
        await repository.UpsertAsync(profile with { Name = "Renamed" }, CancellationToken.None);

        OptimizationProfile? loaded = await repository.GetAsync("competitive", CancellationToken.None);

        Assert.Equal("Renamed", loaded!.Name);
        Assert.Single(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Profile_BuiltInProfilesCannotBeDeleted()
    {
        var repository = new ProfileRepository(Factory);
        await repository.UpsertAsync(
            Profile("balanced", ProfileKind.Balanced) with { IsBuiltIn = true }, CancellationToken.None);

        Assert.False(await repository.DeleteAsync("balanced", CancellationToken.None));
        Assert.NotNull(await repository.GetAsync("balanced", CancellationToken.None));
    }

    [Fact]
    public async Task AppliedTweak_TracksWhatThisProductChanged()
    {
        var repository = new AppliedTweakRepository(Factory);
        var journal = new TransactionJournal(Factory);
        var transactionId = Guid.NewGuid();

        await journal.CreateTransactionAsync(
            new OptimizationTransaction
            {
                Id = transactionId,
                Status = TransactionStatus.Applied,
                Reason = TransactionReason.GamingSession,
                HardwareFingerprint = "fingerprint",
                StartedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        await repository.UpsertAsync(
            new AppliedTweakRecord("cpu.affinity", transactionId, 1, TweakScope.Session, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Single(await repository.GetAllAsync(CancellationToken.None));

        await repository.RemoveAsync("cpu.affinity", CancellationToken.None);

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Audit_RoundTripsItsDetails()
    {
        var repository = new AuditRepository(Factory);

        await repository.WriteAsync(
            new OperationAuditRecord
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                Module = "Core.Test",
                Action = "apply",
                Target = "cpu.affinity",
                OriginalValue = "0xFF",
                NewValue = "0x0F",
                Outcome = AuditOutcome.Success,
                Details = new Dictionary<string, string> { ["mask"] = "0x0F" },
            },
            CancellationToken.None);

        OperationAuditRecord record = Assert.Single(
            await repository.GetRecentAsync(10, CancellationToken.None));

        Assert.Equal("cpu.affinity", record.Target);
        Assert.Equal("0x0F", record.Details["mask"]);
    }

    [Fact]
    public async Task Benchmark_RoundTripsFrameStatistics()
    {
        var repository = new BenchmarkRepository(Factory);
        var run = new BenchmarkRun
        {
            Id = Guid.NewGuid(),
            HardwareFingerprint = "fingerprint",
            WorkloadId = "game.exe",
            Label = "baseline",
            StartedAtUtc = DateTimeOffset.UtcNow,
            FrameTimes = new FrameTimeStatistics
            {
                SampleCount = 1000,
                Duration = TimeSpan.FromSeconds(16),
                MeanFrameTimeMs = 16d,
                MedianFrameTimeMs = 15.8d,
                P95FrameTimeMs = 18d,
                P99FrameTimeMs = 22d,
                P999FrameTimeMs = 40d,
                StandardDeviationMs = 2.5d,
            },
            AppliedTweakIds = new[] { "cpu.affinity" },
        };

        await repository.SaveAsync(run, CancellationToken.None);

        BenchmarkRun loaded = Assert.Single(
            await repository.GetRunsAsync("fingerprint", "game.exe", 10, CancellationToken.None));

        Assert.Equal(22d, loaded.FrameTimes!.P99FrameTimeMs);
        Assert.Equal("cpu.affinity", Assert.Single(loaded.AppliedTweakIds));
    }

    [Fact]
    public async Task Benchmark_ResultsAreScopedToOneMachineAndWorkload()
    {
        var repository = new BenchmarkRepository(Factory);

        await repository.SaveAsync(Run("machine-a", "game.exe"), CancellationToken.None);
        await repository.SaveAsync(Run("machine-b", "game.exe"), CancellationToken.None);

        // A measurement from other hardware is not evidence about this machine, so it must not
        // be reachable through this query.
        Assert.Single(await repository.GetRunsAsync("machine-a", "game.exe", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Settings_RoundTripAndOverwrite()
    {
        var repository = new SettingsRepository(Factory);

        Assert.Null(await repository.GetAsync("mode", CancellationToken.None));

        await repository.SetAsync("mode", "easy", CancellationToken.None);
        await repository.SetAsync("mode", "expert", CancellationToken.None);

        Assert.Equal("expert", await repository.GetAsync("mode", CancellationToken.None));
    }

    private static BenchmarkRun Run(string fingerprint, string workload) => new()
    {
        Id = Guid.NewGuid(),
        HardwareFingerprint = fingerprint,
        WorkloadId = workload,
        Label = "baseline",
        StartedAtUtc = DateTimeOffset.UtcNow,
    };

    private static OptimizationProfile Profile(string id, ProfileKind kind) => new()
    {
        Id = id,
        Name = id,
        Kind = kind,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ModifiedAtUtc = DateTimeOffset.UtcNow,
    };
}
