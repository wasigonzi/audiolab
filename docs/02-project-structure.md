# 2. Project and folder structure

```
Velocity.sln
Directory.Build.props          Shared compiler settings: nullable, warnings-as-errors, analyzers
Directory.Packages.props       Central package version management
global.json                    SDK pin (10.0.100, rollForward latestFeature)
nuget.config                   Single source: nuget.org

src/
  Velocity.Abstractions/       net10.0 — contracts only, no platform code
    Hardware/                  CpuTopology, CpuLayout, GpuDevice, MemoryInfo, StorageDevice,
                               NetworkAdapter, DisplayDevice, PowerConfiguration, SystemProfile,
                               HardwareFingerprint, probe interfaces
    Tweaks/                    ITweak, TweakDescriptor, TweakContext, results, enums
    State/                     StateKey, StateValue, IStateProvider, IStateAccessor
    Transactions/              OptimizationTransaction, TransactionStep, StateSnapshot
    Profiles/                  OptimizationProfile, ProfileKind
    Telemetry/                 FrameTimeStatistics, BenchmarkRun, MetricComparison, TrialVerdict
    Privileges/                IPrivilegeContext, PrivilegeChannel
    Diagnostics/               OperationAuditRecord, ISensitiveDataRedactor
    Hosting/                   IVelocityPaths

  Velocity.Diagnostics/        net10.0 — logging and support bundles
    Logging/                   Serilog wiring, redaction, runtime verbosity control, bundle export
    VelocityPaths.cs           %ProgramData%\Velocity layout

  Velocity.Data/               net10.0 — SQLite persistence
    Migrations/                001_initial_schema.sql (embedded), DatabaseMigrator
    Repositories/              TransactionJournal, AppliedTweakRepository, AuditRepository,
                               SystemProfileRepository, ProfileRepository, BenchmarkRepository,
                               SettingsRepository
    SqliteConnectionFactory.cs WAL, busy timeout, per-connection foreign keys

  Velocity.Core/               net10.0 — the engine
    Hardware/                  CpuTopologyAnalyzer, HardwareFingerprintFactory, SystemProfileProvider
    State/                     StateProviderRegistry, TransactionalStateAccessor, InMemoryStateProvider
    Tweaks/                    TweakRegistry, CompatibilityEvaluator
    Transactions/              OptimizationEngine, RollbackEngine, CrashRecoveryService,
                               TweakContextFactory
    Statistics/                DescriptiveStatistics, SignificanceTest, TrialEvaluator
    Auditing/                  AuditSink, AuditRecordFactory

  Velocity.Ipc/                net10.0 — helper protocol, transport independent
    Protocol/                  IpcRequest, IpcResponse, operations, error codes, JSON context
    Framing/                   MessageFramer (length-prefixed JSON)
    Authorization/             PrivilegedOperationPolicy — the security boundary
    IpcServer.cs, IpcClient.cs

  Velocity.Platform.Windows/   net10.0-windows10.0.26100.0
    Interop/                   NativeMethods (LibraryImport), WmiQuery
    Probes/                    Cpu topology, OS, memory, GPU, storage, network, display, power,
                               platform security, machine kind
    State/                     RegistryStateProvider
    Privileges/                WindowsPrivilegeContext
    Ipc/                       NamedPipeServerHost (ACL + caller verification), client channel

  Velocity.Helper/             net10.0-windows — SYSTEM service, one fixed operation list
    Handlers/                  Ping, GetIdentity, RegistryRead, RegistryWrite

  Velocity.Presentation/       net10.0 — MVVM, no XAML, no WinUI reference
    Mvvm/                      ViewModelBase, IUiDispatcher, ApplicationMode
    Navigation/                INavigationService, NavigationCatalogue
    ViewModels/                ShellViewModel, SystemInformationViewModel

  Velocity.Cli/                net10.0-windows — headless host and composition root

tests/
  Velocity.TestSupport/        Machine fixtures (Intel desktop, Intel hybrid, single/dual-CCD
                               Ryzen, laptop, dual processor group) and fakes
  Velocity.Core.Tests/         87 tests: topology, fingerprint, compatibility, transactions,
                               rollback, crash recovery, statistics, state accessor
  Velocity.Data.Tests/         18 tests: migrations, journal, repositories
  Velocity.Ipc.Tests/          41 tests: framing, dispatch, replay rejection, policy
  Velocity.Presentation.Tests/ 18 tests: view models, navigation, privilege banner

docs/                          This documentation set
```

## Projects still to be created

| Project | Why not yet |
| --- | --- |
| `Velocity.Setup` (MSI/MSIX) | Needs a signing identity and a service registration story — see [11-status.md](11-status.md) |
| `Velocity.Platform.Windows.Tests` | Requires a Windows CI leg that actually runs, not merely compiles. The logic that can be tested without Windows already is |
