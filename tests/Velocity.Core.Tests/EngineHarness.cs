using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Auditing;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// Wires the real engine against a real SQLite database (in memory) and an in-memory state
/// provider.
/// </summary>
/// <remarks>
/// Nothing here is a mock of the engine itself. The transaction coordinator, journal, snapshot
/// capture, rollback engine and recovery service under test are exactly the types that ship; only
/// the machine underneath them is substituted.
/// </remarks>
internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _connectionFactory;

    private EngineHarness(
        SqliteConnectionFactory connectionFactory,
        InMemoryStateProvider stateProvider,
        ITransactionJournal journal,
        IAppliedTweakRepository appliedTweaks,
        IAuditRepository auditRepository,
        ITweakRegistry registry,
        ITweakContextFactory contextFactory,
        IRollbackEngine rollback,
        IOptimizationEngine engine,
        ICrashRecoveryService recovery)
    {
        _connectionFactory = connectionFactory;
        StateProvider = stateProvider;
        Journal = journal;
        AppliedTweaks = appliedTweaks;
        AuditRepository = auditRepository;
        Registry = registry;
        ContextFactory = contextFactory;
        Rollback = rollback;
        Engine = engine;
        Recovery = recovery;
    }

    internal InMemoryStateProvider StateProvider { get; }

    internal ITransactionJournal Journal { get; }

    internal IAppliedTweakRepository AppliedTweaks { get; }

    internal IAuditRepository AuditRepository { get; }

    internal ITweakRegistry Registry { get; }

    internal ITweakContextFactory ContextFactory { get; }

    internal IRollbackEngine Rollback { get; }

    internal IOptimizationEngine Engine { get; }

    internal ICrashRecoveryService Recovery { get; }

    internal static async Task<EngineHarness> CreateAsync(
        IEnumerable<ITweak> tweaks,
        SystemProfile? profile = null,
        PrivilegeChannel privilegeChannel = PrivilegeChannel.HelperService)
    {
        SqliteConnectionFactory connectionFactory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(connectionFactory, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);

        var journal = new TransactionJournal(connectionFactory);
        var appliedTweaks = new AppliedTweakRepository(connectionFactory);
        var auditRepository = new AuditRepository(connectionFactory);
        var audit = new AuditSink(auditRepository, new PassThroughRedactor(), NullLogger<AuditSink>.Instance);

        var stateProvider = new InMemoryStateProvider();
        var providerRegistry = new StateProviderRegistry(new IStateProvider[] { stateProvider });
        var profileProvider = new FakeSystemProfileProvider(
            profile ?? MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore()));
        var privileges = new FakePrivilegeContext(privilegeChannel);

        var contextFactory = new TweakContextFactory(
            profileProvider, providerRegistry, privileges, NullLoggerFactory.Instance);
        var registry = new TweakRegistry(tweaks);

        var rollback = new RollbackEngine(
            journal, registry, contextFactory, appliedTweaks, audit, NullLogger<RollbackEngine>.Instance);

        var engine = new OptimizationEngine(
            registry, contextFactory, profileProvider, journal, appliedTweaks, audit, rollback,
            NullLogger<OptimizationEngine>.Instance);

        var recovery = new CrashRecoveryService(
            journal, rollback, audit, NullLogger<CrashRecoveryService>.Instance);

        return new EngineHarness(
            connectionFactory, stateProvider, journal, appliedTweaks, auditRepository,
            registry, contextFactory, rollback, engine, recovery);
    }

    public ValueTask DisposeAsync()
    {
        _connectionFactory.Dispose();
        return ValueTask.CompletedTask;
    }
}
