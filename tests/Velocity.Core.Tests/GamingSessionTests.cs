using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Games;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Games;
using Velocity.Core.Profiles;
using Velocity.Core.Sessions;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.Data.Repositories;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers the Optimize and Launch pipeline.
/// </summary>
/// <remarks>
/// The property under test throughout is that the machine is restored. A session that fails to
/// launch, times out, is cancelled or throws must still put back everything it captured, and these
/// tests drive the real engine and the real rollback engine to prove it.
/// </remarks>
public sealed class GamingSessionTests
{
    private static readonly StateKey Key = new("registry", @"HKLM\Test\Path", "Value");

    [Fact]
    public async Task ASessionAppliesTheProfileLaunchesAndRestoresOnExit()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync();

        GameSessionReport report = await harness.RunAsync();

        Assert.Equal(GameSessionState.Completed, report.State);
        Assert.Equal(["test.tweak"], report.AppliedTweakIds);
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
        Assert.NotNull(report.TransactionId);
    }

    [Fact]
    public async Task AFailedLaunchStillRestoresEverythingThatWasApplied()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync(
            launchResult: new GameLaunchResult(false, null, false, "The executable is missing."));

        GameSessionReport report = await harness.RunAsync();

        Assert.Equal(GameSessionState.Failed, report.State);
        Assert.Equal("The executable is missing.", report.Error);

        // The failure is in starting the game, not in changing the machine, so the machine goes back.
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
    }

    [Fact]
    public async Task AGameThatNeverAppearsAfterAStoreLaunchTimesOutAndRestores()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync(
            launchResult: new GameLaunchResult(true, ProcessId: 999, IsGameProcess: false),
            detected: []);

        GameSessionReport report = await harness.RunAsync();

        Assert.Equal(GameSessionState.Failed, report.State);
        Assert.Contains("did not appear", report.Error, StringComparison.Ordinal);
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
    }

    [Fact]
    public async Task CancellingASessionRestoresRatherThanAbandoningTheMachine()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync(gameKeepsRunning: true);

        using var cancellation = new CancellationTokenSource();
        Task<GameSessionReport> running = harness.RunAsync(cancellation.Token);

        await harness.WaitForStateAsync(GameSessionState.Running);
        await cancellation.CancelAsync();

        GameSessionReport report = await running;

        Assert.Equal(GameSessionState.Completed, report.State);
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
    }

    [Fact]
    public async Task AFailedRestoreIsReportedRatherThanCalledSuccess()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync(restoreThrows: true);

        GameSessionReport report = await harness.RunAsync();

        Assert.Equal(GameSessionState.CompletedWithRestoreFailures, report.State);
        Assert.NotEmpty(report.RestoreFailures);
    }

    [Fact]
    public async Task ASessionWithNoProfileRunsUnoptimizedAndChangesNothing()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync(withProfile: false);

        GameSessionReport report = await harness.RunAsync();

        Assert.Equal(GameSessionState.Completed, report.State);
        Assert.Null(report.TransactionId);
        Assert.Empty(report.AppliedTweakIds);
        Assert.Equal(0, harness.RegistryState.WriteCount);
    }

    [Fact]
    public async Task ASessionReportCarriesResourceTelemetryAndNeverAFrameRateClaim()
    {
        await using SessionHarness harness = await SessionHarness.CreateAsync();

        GameSessionReport report = await harness.RunAsync();

        // ResourceStatistics has no frame time member at all: a session has no before measurement,
        // so it cannot support a statement about frames.
        Assert.Null(report.Telemetry);
        Assert.Equal(0, report.TelemetrySampleCount);
    }

    [Fact]
    public void TheAccumulatorAveragesOnlyTheCountersThatWerePresent()
    {
        var accumulator = new TelemetryAccumulator();

        accumulator.Add(new TelemetrySample
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            CpuUtilization = 0.40,
            GpuUtilization = null,
        });

        accumulator.Add(new TelemetrySample
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            CpuUtilization = 0.60,
            GpuUtilization = 0.90,
        });

        ResourceStatistics? summary = accumulator.Summarize();

        Assert.NotNull(summary);
        Assert.Equal(0.50, summary.MeanCpuUtilization, 6);

        // The missing GPU sample must not be averaged in as a zero.
        Assert.Equal(0.90, summary.MeanGpuUtilization, 6);
    }

    [Fact]
    public void AnAccumulatorThatSawNothingSummarizesToNothing()
    {
        Assert.Null(new TelemetryAccumulator().Summarize());
    }

    /// <summary>Drives a real engine, journal and rollback engine around the session manager.</summary>
    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly EngineHarness _engine;
        private readonly GamingSessionManager _manager;
        private readonly GameInstallation _game;
        private readonly List<GameSessionReport> _states = [];

        private SessionHarness(
            EngineHarness engine,
            GamingSessionManager manager,
            GameInstallation game)
        {
            _engine = engine;
            _manager = manager;
            _game = game;

            manager.SessionChanged += (_, report) =>
            {
                lock (_states)
                {
                    _states.Add(report);
                }
            };
        }

        public InMemoryStateProvider RegistryState => _engine.RegistryState;

        public static async Task<SessionHarness> CreateAsync(
            GameLaunchResult? launchResult = null,
            IReadOnlyList<DetectedGame>? detected = null,
            bool withProfile = true,
            bool gameKeepsRunning = false,
            bool restoreThrows = false)
        {
            EngineHarness engine = await EngineHarness.CreateAsync([new SessionTweak()]);
            engine.RegistryState.Seed(Key, StateValue.FromUInt32(1));

            var game = new GameInstallation
            {
                Id = "steam:1",
                Name = "Test Game",
                ExecutablePath = @"C:\Games\Test\Test.exe",
            };

            var profiles = new StubProfileService(withProfile);

            var manager = new GamingSessionManager(
                profiles,
                engine.Engine,
                restoreThrows
                    ? new ThrowingRollbackEngine()
                    : engine.Rollback,
                new FakeGameLauncher(launchResult ?? new GameLaunchResult(true, 42, IsGameProcess: true)),
                new FakeGameDetector(detected ?? []),
                new ScriptedProcessInspector(gameKeepsRunning),
                TimeProvider.System,

                // Real time at millisecond scale: the pipeline's ordering is what is under test,
                // not its patience, and a virtual clock here only adds a scheduler race.
                new GamingSessionOptions
                {
                    LaunchTimeout = TimeSpan.FromMilliseconds(150),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    ExitGracePeriod = TimeSpan.FromMilliseconds(5),
                },
                NullLogger<GamingSessionManager>.Instance);

            return new SessionHarness(engine, manager, game);
        }

        public Task<GameSessionReport> RunAsync(CancellationToken cancellationToken = default) =>
            _manager.OptimizeAndLaunchAsync(_game, cancellationToken);

        public Task<StateValue> ReadAsync() => _engine.RegistryState.ReadAsync(Key, CancellationToken.None);

        public async Task WaitForStateAsync(GameSessionState state)
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                lock (_states)
                {
                    if (_states.Any(report => report.State == state))
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"The session never reached {state}.");
        }

        public ValueTask DisposeAsync() => _engine.DisposeAsync();
    }

    /// <summary>A minimal module so the session has something real to apply and roll back.</summary>
    private sealed class SessionTweak : ITweak
    {
        public TweakDescriptor Descriptor { get; } = new()
        {
            Id = "test.tweak",
            Name = "Session test module",
            Category = TweakCategory.Cpu,
            Summary = "Writes one value.",
            TechnicalDescription = "Writes one registry value, for tests.",
            ExpectedEffect = "None; this module exists only in tests.",
            Risk = RiskLevel.Low,
            Scope = TweakScope.Session,
            RequiresElevation = false,
            RequiresRestart = false,
            BenchmarkRecommended = false,
            DefinitionVersion = 1,
        };

        public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
            TweakContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StateKey>>([Key]);

        public Task<CompatibilityResult> CheckCompatibilityAsync(
            TweakContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CompatibilityResult.Supported("Always."));

        public async Task<TweakObservation> DetectAsync(
            TweakContext context, CancellationToken cancellationToken) =>
            new()
            {
                State = (await context.State.ReadAsync(Key, cancellationToken)).AsUInt32() == 2
                    ? AppliedState.Applied
                    : AppliedState.NotApplied,
                CurrentValueSummary = "test",
                RecommendedValueSummary = "test",
            };

        public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
        {
            await context.State.WriteAsync(Key, StateValue.FromUInt32(2), cancellationToken);
            return ApplyResult.Applied("Written.", Key);
        }

        public async Task<VerificationResult> VerifyAsync(
            TweakContext context, CancellationToken cancellationToken) =>
            (await context.State.ReadAsync(Key, cancellationToken)).AsUInt32() == 2
                ? VerificationResult.Verified("Written.")
                : VerificationResult.Mismatch("Not written.");
    }

    private sealed class StubProfileService : IProfileService
    {
        private readonly bool _hasProfile;

        public StubProfileService(bool hasProfile) => _hasProfile = hasProfile;

        public Task<IReadOnlyList<OptimizationProfile>> GetProfilesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptimizationProfile>>([]);

        public Task<OptimizationProfile?> GetProfileAsync(string profileId, CancellationToken ct) =>
            Task.FromResult<OptimizationProfile?>(null);

        public Task<OptimizationProfile?> ResolveForGameAsync(string? gameId, CancellationToken ct) =>
            Task.FromResult(_hasProfile ? Profile() : null);

        public Task SaveAsync(OptimizationProfile profile, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string profileId, CancellationToken ct) => Task.FromResult(false);

        public Task SetDefaultProfileAsync(string? profileId, CancellationToken ct) => Task.CompletedTask;

        public OptimizationRequest BuildRequest(
            OptimizationProfile profile, TransactionReason reason, Guid? sessionId = null) => new()
        {
            TweakIds = ["test.tweak"],
            Reason = reason,
            ProfileId = profile.Id,
            SessionId = sessionId,
            AtomicAllOrNothing = false,
        };

        private static OptimizationProfile Profile() => new()
        {
            Id = "test.profile",
            Name = "Test",
            Kind = ProfileKind.Balanced,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            ModifiedAtUtc = DateTimeOffset.UnixEpoch,
            Tweaks = [new ProfileTweakSetting("test.tweak", true, new Dictionary<string, string>())],
        };
    }

    /// <summary>Reports the game as gone on the second poll, unless told to keep it alive.</summary>
    private sealed class ScriptedProcessInspector : IProcessInspector
    {
        private readonly bool _keepRunning;
        private int _polls;

        public ScriptedProcessInspector(bool keepRunning) => _keepRunning = keepRunning;

        public Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);

        public Task<ProcessSnapshot?> GetProcessAsync(int processId, CancellationToken ct)
        {
            _polls++;

            return Task.FromResult(_keepRunning || _polls <= 1
                ? new ProcessSnapshot { ProcessId = processId, ExecutableName = "Test.exe" }
                : null);
        }
    }

    private sealed class ThrowingRollbackEngine : IRollbackEngine
    {
        public Task<RollbackResult> RollbackTransactionAsync(Guid transactionId, CancellationToken ct) =>
            throw new InvalidOperationException("The state provider is gone.");

        public Task<RollbackResult?> RollbackLastAsync(CancellationToken ct) =>
            Task.FromResult<RollbackResult?>(null);

        public Task<RollbackResult?> RollbackTweakAsync(string tweakId, CancellationToken ct) =>
            Task.FromResult<RollbackResult?>(null);
    }
}
