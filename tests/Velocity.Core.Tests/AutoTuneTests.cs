using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.AutoTune;
using Velocity.Core.Benchmarking;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers the auto-tune loop.
/// </summary>
/// <remarks>
/// The rules under test are the ones that separate measurement from marketing: one change at a
/// time, neutral results are reverted, nothing is concluded without frames, a restart-gated setting
/// is not measured at all, and results never leave the machine they were measured on.
/// </remarks>
public sealed class AutoTuneTests : IAsyncLifetime
{
    private static readonly StateKey Key = new("registry", @"HKLM\Test\AutoTune", "Value");

    private SqliteConnectionFactory _connectionFactory = null!;
    private TrialResultRepository _results = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connectionFactory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(_connectionFactory, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        _results = new TrialResultRepository(_connectionFactory);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _connectionFactory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void OptionsHashIsStableAcrossInsertionOrder()
    {
        // Without this, re-running the same trial would create a second row instead of adding
        // evidence to the first.
        string first = AutoTuneHash.ForOptions(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "1",
            ["b"] = "2",
        });

        string second = AutoTuneHash.ForOptions(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["b"] = "2",
            ["a"] = "1",
        });

        Assert.Equal(first, second);
        Assert.NotEqual(first, AutoTuneHash.ForOptions(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "2", ["b"] = "2" }));
        Assert.Equal("default", AutoTuneHash.ForOptions(null));
    }

    [Fact]
    public void ARestartGatedModuleIsNeverPutThroughASingleSittingTrial()
    {
        Assert.False(AutoTunePlanner.IsTrialable(Descriptor(benchmark: true, restart: true)));
        Assert.False(AutoTunePlanner.IsTrialable(Descriptor(benchmark: false, restart: false)));
        Assert.True(AutoTunePlanner.IsTrialable(Descriptor(benchmark: true, restart: false)));
    }

    [Fact]
    public async Task WithoutFrameCaptureNothingIsAppliedAtAll()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results, frames: new UnavailableFrameTimeSource("Needs an elevated trace session."));

        AutoTuneReport report = await harness.RunAsync();

        Assert.Equal(AutoTuneOutcome.NotMeasured, Assert.Single(report.Candidates).Outcome);
        Assert.Contains("elevated", report.StoppedBecause, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.RegistryState.WriteCount);
    }

    [Fact]
    public async Task AChangeThatMeasurablyHelpsIsKept()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 20.0d, candidateMs: 16.7d));

        AutoTuneReport report = await harness.RunAsync();

        AutoTuneCandidateReport candidate = Assert.Single(report.Candidates);
        Assert.Equal(AutoTuneOutcome.Kept, candidate.Outcome);

        // Kept means left applied on the machine.
        Assert.Equal(StateValue.FromUInt32(2), await harness.ReadAsync());
        Assert.Single(report.Kept);
    }

    [Fact]
    public async Task AChangeThatMeasuresAsNoDifferenceIsReverted()
    {
        // An unnecessary modification to someone's operating system is a cost even at zero frames.
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 16.70d, candidateMs: 16.69d));

        AutoTuneReport report = await harness.RunAsync();

        Assert.Equal(AutoTuneOutcome.Reverted, Assert.Single(report.Candidates).Outcome);
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
    }

    [Fact]
    public async Task AChangeThatWorsensTheTailIsReverted()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 16.7d, candidateMs: 19.5d));

        AutoTuneReport report = await harness.RunAsync();

        Assert.Equal(AutoTuneOutcome.Reverted, Assert.Single(report.Candidates).Outcome);
        Assert.Equal(StateValue.FromUInt32(1), await harness.ReadAsync());
    }

    [Fact]
    public async Task AMeasuredResultIsStoredAgainstThisMachineAndWorkload()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 20.0d, candidateMs: 16.7d));

        AutoTuneReport report = await harness.RunAsync();

        TweakTrialResult stored = Assert.Single(await _results.GetResultsAsync(
            report.HardwareFingerprint, "Test Game", CancellationToken.None));

        Assert.Equal("test.tweak", stored.TweakId);
        Assert.Equal(TrialDecision.Keep, stored.Decision);
        Assert.NotNull(stored.BaselineP99Ms);
        Assert.NotNull(stored.CandidateP99Ms);
        Assert.Equal(1, stored.TrialCount);

        // Another machine's history is a different scope and must not be reachable from here.
        Assert.Empty(await _results.GetResultsAsync(
            "some-other-machine", "Test Game", CancellationToken.None));
    }

    [Fact]
    public async Task ReRunningTheSameTrialAccumulatesEvidenceRatherThanDuplicatingIt()
    {
        var result = new TweakTrialResult
        {
            HardwareFingerprint = "fp",
            WorkloadId = "Game",
            TweakId = "test.tweak",
            OptionsHash = "default",
            Decision = TrialDecision.Keep,
            Rationale = "Measured improvement.",
            EvaluatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        await _results.UpsertAsync(result, CancellationToken.None);
        await _results.UpsertAsync(result with { Rationale = "Measured again." }, CancellationToken.None);

        TweakTrialResult? stored = await _results.GetResultAsync(
            "fp", "Game", "test.tweak", "default", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(2, stored.TrialCount);
        Assert.Equal("Measured again.", stored.Rationale);
    }

    [Fact]
    public async Task AnAlreadyMeasuredCandidateIsNotMeasuredAgain()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 20.0d, candidateMs: 16.7d));

        await _results.UpsertAsync(
            new TweakTrialResult
            {
                HardwareFingerprint = harness.Fingerprint,
                WorkloadId = "Test Game",
                TweakId = "test.tweak",
                OptionsHash = "default",
                Decision = TrialDecision.Revert,
                Rationale = "Measured last week; it did not help.",
                EvaluatedAtUtc = DateTimeOffset.UnixEpoch,
            },
            CancellationToken.None);

        AutoTuneReport report = await harness.RunAsync();

        AutoTuneCandidateReport candidate = Assert.Single(report.Candidates);
        Assert.Equal(AutoTuneOutcome.AlreadyMeasured, candidate.Outcome);
        Assert.Equal(0, harness.RegistryState.WriteCount);
    }

    [Fact]
    public async Task AModuleThisBuildDoesNotHaveIsRejectedRatherThanApplied()
    {
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 20.0d, candidateMs: 16.7d));

        AutoTuneReport report = await harness.RunAsync(
            new AutoTuneCandidate("does.not.exist", new Dictionary<string, string>(), "Nonexistent"));

        Assert.Equal(AutoTuneOutcome.Incompatible, Assert.Single(report.Candidates).Outcome);
        Assert.Equal(0, harness.RegistryState.WriteCount);
    }

    [Fact]
    public async Task EachCandidateIsTrialedOnItsOwn()
    {
        // Applying several and measuring once produces a number that cannot be attributed.
        await using AutoTuneHarness harness = await AutoTuneHarness.CreateAsync(
            _results,
            frames: new StagedFrameSource(baselineMs: 20.0d, candidateMs: 16.7d),
            extraTweak: true);

        AutoTuneReport report = await harness.RunAsync(
            new AutoTuneCandidate("test.tweak", new Dictionary<string, string>(), "First"),
            new AutoTuneCandidate("test.second", new Dictionary<string, string>(), "Second"));

        Assert.Equal(2, report.Candidates.Count);
        Assert.All(report.Candidates, candidate => Assert.NotNull(candidate.Result));

        // Two candidates means two measured trials, each with its own stored row.
        Assert.Equal(2, (await _results.GetResultsAsync(
            report.HardwareFingerprint, "Test Game", CancellationToken.None)).Count);
    }

    private static TweakDescriptor Descriptor(bool benchmark, bool restart) => new()
    {
        Id = "x",
        Name = "x",
        Category = TweakCategory.Cpu,
        Summary = "x",
        TechnicalDescription = "x",
        ExpectedEffect = "x",
        Risk = RiskLevel.Low,
        Scope = TweakScope.Session,
        RequiresElevation = false,
        RequiresRestart = restart,
        BenchmarkRecommended = benchmark,
        DefinitionVersion = 1,
    };

    /// <summary>Drives the real engine, journal, rollback engine and benchmark lab.</summary>
    private sealed class AutoTuneHarness : IAsyncDisposable
    {
        private readonly EngineHarness _engine;
        private readonly AutoTuneEngine _autoTune;

        private AutoTuneHarness(EngineHarness engine, AutoTuneEngine autoTune, string fingerprint)
        {
            _engine = engine;
            _autoTune = autoTune;
            Fingerprint = fingerprint;
        }

        public string Fingerprint { get; }

        public InMemoryStateProvider RegistryState => _engine.RegistryState;

        public static async Task<AutoTuneHarness> CreateAsync(
            ITrialResultRepository results,
            IFrameTimeSource frames,
            bool extraTweak = false)
        {
            ITweak[] tweaks = extraTweak
                ? [new TrialTweak("test.tweak"), new TrialTweak("test.second")]
                : [new TrialTweak("test.tweak")];

            EngineHarness engine = await EngineHarness.CreateAsync(tweaks);
            engine.RegistryState.Seed(Key, StateValue.FromUInt32(1));

            var machine = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());
            var profiles = new FakeSystemProfileProvider(machine);

            var lab = new BenchmarkLab(
                frames,
                new FrameTimeRecorder(NullLogger<FrameTimeRecorder>.Instance),
                new InMemoryBenchmarkRepository(),
                profiles,
                TimeProvider.System,
                NullLogger<BenchmarkLab>.Instance);

            var autoTune = new AutoTuneEngine(
                lab,
                engine.Engine,
                engine.Rollback,
                engine.Registry,
                results,
                profiles,
                TimeProvider.System,
                NullLogger<AutoTuneEngine>.Instance);

            return new AutoTuneHarness(engine, autoTune, machine.Fingerprint.CompositeHash);
        }

        public Task<AutoTuneReport> RunAsync(params AutoTuneCandidate[] candidates) =>
            _autoTune.RunAsync(
                new AutoTuneRequest
                {
                    WorkloadId = "Test Game",
                    MeasurementDuration = TimeSpan.FromMilliseconds(200),
                    ProfileKind = ProfileKind.Competitive,
                    Candidates = candidates.Length > 0
                        ? candidates
                        : [new AutoTuneCandidate("test.tweak", new Dictionary<string, string>(), "Test")],
                },
                progress: null,
                CancellationToken.None);

        public Task<StateValue> ReadAsync() =>
            _engine.RegistryState.ReadAsync(Key, CancellationToken.None);

        public ValueTask DisposeAsync() => _engine.DisposeAsync();
    }

    /// <summary>A module that writes one value, so a trial has something real to apply and revert.</summary>
    private sealed class TrialTweak : ITweak
    {
        public TrialTweak(string id) => Descriptor = new TweakDescriptor
        {
            Id = id,
            Name = id,
            Category = TweakCategory.Cpu,
            Summary = "Writes one value.",
            TechnicalDescription = "Writes one registry value, for tests.",
            ExpectedEffect = "None; this module exists only in tests.",
            Risk = RiskLevel.Low,
            Scope = TweakScope.Session,
            RequiresElevation = false,
            RequiresRestart = false,
            BenchmarkRecommended = true,
            DefinitionVersion = 1,
        };

        public TweakDescriptor Descriptor { get; }

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

    /// <summary>Replays one frame time series before the candidate applies, another after.</summary>
    private sealed class StagedFrameSource : IFrameTimeSource
    {
        private readonly double _baselineMs;
        private readonly double _candidateMs;
        private int _captures;

        public StagedFrameSource(double baselineMs, double candidateMs)
        {
            _baselineMs = baselineMs;
            _candidateMs = candidateMs;
        }

        public Task<FrameCaptureStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FrameCaptureStatus(FrameCaptureAvailability.Available, "ok"));

        public async IAsyncEnumerable<FramePresentEvent> CaptureAsync(
            int processId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Odd captures are baselines, even ones are candidates.
            bool isBaseline = Interlocked.Increment(ref _captures) % 2 == 1;
            double mean = isBaseline ? _baselineMs : _candidateMs;

            var random = new Random(20260101);
            double timestamp = 0d;

            for (int frame = 0; frame < 800; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timestamp += mean + ((random.NextDouble() - 0.5d) * 0.6d);
                yield return new FramePresentEvent(processId, timestamp);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Benchmark storage that keeps runs in memory, so the lab has somewhere to save.</summary>
    private sealed class InMemoryBenchmarkRepository : IBenchmarkRepository
    {
        private readonly List<BenchmarkRun> _runs = [];

        public Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BenchmarkRun>> GetRunsAsync(
            string hardwareFingerprint,
            string workloadId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BenchmarkRun>>(
            [
                .. _runs
                    .Where(run => run.HardwareFingerprint == hardwareFingerprint &&
                                  run.WorkloadId == workloadId)
                    .Take(limit)
            ]);
    }
}
