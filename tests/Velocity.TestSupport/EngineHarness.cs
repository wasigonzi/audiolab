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

namespace Velocity.TestSupport;

/// <summary>
/// Wires the real engine against a real SQLite database (in memory) and an in-memory state
/// provider.
/// </summary>
/// <remarks>
/// Nothing here is a mock of the engine itself. The transaction coordinator, journal, snapshot
/// capture, rollback engine and recovery service under test are exactly the types that ship; only
/// the machine underneath them is substituted.
/// </remarks>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _connectionFactory;

    private EngineHarness(
        SqliteConnectionFactory connectionFactory,
        InMemoryStateProvider stateProvider,
        InMemoryStateProvider registryState,
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
        RegistryState = registryState;
        Journal = journal;
        AppliedTweaks = appliedTweaks;
        AuditRepository = auditRepository;
        Registry = registry;
        ContextFactory = contextFactory;
        Rollback = rollback;
        Engine = engine;
        Recovery = recovery;
    }

    /// <summary>The in-memory machine the engine writes to, under the <c>memory</c> scheme.</summary>
    public InMemoryStateProvider StateProvider { get; }

    /// <summary>
    /// The same thing under the <c>registry</c> scheme, so a production module — which addresses
    /// real registry keys — runs against the real engine with no Windows present.
    /// </summary>
    public InMemoryStateProvider RegistryState { get; }

    /// <summary>The real SQLite journal.</summary>
    public ITransactionJournal Journal { get; }

    /// <summary>Record of what the engine has applied.</summary>
    public IAppliedTweakRepository AppliedTweaks { get; }

    /// <summary>Audit log storage.</summary>
    public IAuditRepository AuditRepository { get; }

    /// <summary>The catalogue built from the supplied modules.</summary>
    public ITweakRegistry Registry { get; }

    /// <summary>Factory for module execution contexts.</summary>
    public ITweakContextFactory ContextFactory { get; }

    /// <summary>The real rollback engine.</summary>
    public IRollbackEngine Rollback { get; }

    /// <summary>The real transaction coordinator.</summary>
    public IOptimizationEngine Engine { get; }

    /// <summary>The real startup recovery service.</summary>
    public ICrashRecoveryService Recovery { get; }

    /// <summary>Builds a harness around the supplied modules.</summary>
    /// <param name="tweaks">Modules to register in the catalogue.</param>
    /// <param name="profile">Machine to run against; defaults to an Intel desktop fixture.</param>
    /// <param name="privilegeChannel">Privileges to report.</param>
    /// <returns>The harness.</returns>
    public static async Task<EngineHarness> CreateAsync(
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
        var registryState = new InMemoryStateProvider("registry");
        var providerRegistry = new StateProviderRegistry(
            new IStateProvider[] { stateProvider, registryState });
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
            connectionFactory, stateProvider, registryState, journal, appliedTweaks, auditRepository,
            registry, contextFactory, rollback, engine, recovery);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connectionFactory.Dispose();
        return ValueTask.CompletedTask;
    }
}
