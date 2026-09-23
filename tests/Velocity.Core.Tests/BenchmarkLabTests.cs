using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Core.Benchmarking;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers frame interval extraction and the before/after comparison.
/// </summary>
/// <remarks>
/// The property under test is that the lab refuses to produce a verdict it cannot support: no
/// frames means inconclusive, mismatched hardware or workloads is an error, and a difference below
/// the threshold is reported as no difference however many frames were captured.
/// </remarks>
public sealed class BenchmarkLabTests : IAsyncLifetime
{
    private SqliteConnectionFactory _connectionFactory = null!;
    private BenchmarkRepository _repository = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connectionFactory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(_connectionFactory, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        _repository = new BenchmarkRepository(_connectionFactory);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _connectionFactory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void NPresentsProduceNMinusOneIntervals()
    {
        FrameCapture capture = FrameTimeRecorder.FromPresents([0d, 16.7d, 33.4d, 50.1d]);

        Assert.Equal(3, capture.Count);
        Assert.All(capture.FrameTimesMs, interval => Assert.Equal(16.7d, interval, 1));
        Assert.Empty(capture.DiscardedFrames);
    }

    [Fact]
    public void ASinglePresentProducesNoFrameTime()
    {
        Assert.Equal(0, FrameTimeRecorder.FromPresents([5d]).Count);
        Assert.Equal(0, FrameTimeRecorder.FromPresents([]).Count);
    }

    [Fact]
    public void OutOfOrderEventsAreDroppedAndCounted()
    {
        // Silently dropping them would let a capture flatter itself.
        FrameCapture capture = FrameTimeRecorder.FromPresents([0d, 16d, 10d, 26d]);

        Assert.Equal(2, capture.Count);
        Assert.Equal(1, capture.DiscardedFrames[FrameTimeRecorder.OutOfOrderReason]);
    }

    [Fact]
    public void ALoadingScreenIsNotCountedAsOneVerySlowFrame()
    {
        // A 12 second gap is an alt-tab or a load, and keeping it would dominate every tail
        // percentile in the run.
        FrameCapture capture = FrameTimeRecorder.FromPresents([0d, 16d, 12_016d, 12_032d]);

        Assert.Equal(2, capture.Count);
        Assert.Equal(1, capture.DiscardedFrames[FrameTimeRecorder.StallReason]);
        Assert.All(capture.FrameTimesMs, interval => Assert.True(interval < 100d));
    }

    [Fact]
    public async Task WhenCaptureIsUnavailableNothingIsStoredAndTheReasonIsKept()
    {
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource("No ETW session here."));

        BenchmarkMeasurement measurement = await lab.MeasureAsync(
            Request("baseline"), CancellationToken.None);

        Assert.False(measurement.HasFrameData);
        Assert.Null(measurement.Run);
        Assert.Equal(FrameCaptureAvailability.NotImplementedOnThisPlatform, measurement.Status.Availability);
        Assert.Equal("No ETW session here.", measurement.Status.Detail);
    }

    [Fact]
    public async Task AMeasurementWithFramesIsStoredWithItsHardwareFingerprint()
    {
        BenchmarkLab lab = CreateLab(new ScriptedFrameSource(Frames(16.7d, 600)));

        BenchmarkMeasurement measurement = await lab.MeasureAsync(
            Request("baseline"), CancellationToken.None);

        Assert.True(measurement.HasFrameData);
        Assert.NotNull(measurement.Run);
        Assert.Equal("Test Game", measurement.Run.WorkloadId);
        Assert.NotNull(measurement.Run.FrameTimes);

        IReadOnlyList<BenchmarkRun> stored = await _repository.GetRunsAsync(
            measurement.Run.HardwareFingerprint, "Test Game", 10, CancellationToken.None);

        Assert.Single(stored);
    }

    [Fact]
    public async Task AComparisonWithNoFramesOnEitherSideIsInconclusive()
    {
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource("Needs elevation."));

        BenchmarkMeasurement empty = await lab.MeasureAsync(Request("baseline"), CancellationToken.None);
        BenchmarkComparison comparison = lab.Compare(empty, empty);

        Assert.Equal(TrialDecision.NeedsMoreData, comparison.Verdict.Decision);
        Assert.Contains("Needs elevation.", comparison.Verdict.Rationale, StringComparison.Ordinal);
        Assert.Empty(comparison.Verdict.Comparisons);
    }

    [Fact]
    public void ComparingTwoDifferentWorkloadsIsRefused()
    {
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource());

        ArgumentException error = Assert.Throws<ArgumentException>(() => lab.Compare(
            Measurement("Game A", "fingerprint-1", Frames(16.7d, 400)),
            Measurement("Game B", "fingerprint-1", Frames(16.7d, 400))));

        Assert.Contains("different", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparingRunsFromDifferentMachinesIsRefused()
    {
        // A result measured elsewhere is not evidence about this machine.
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource());

        Assert.Throws<ArgumentException>(() => lab.Compare(
            Measurement("Game A", "fingerprint-1", Frames(16.7d, 400)),
            Measurement("Game A", "fingerprint-2", Frames(16.7d, 400))));
    }

    [Fact]
    public void ARealImprovementIsReportedAsOne()
    {
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource());

        BenchmarkComparison comparison = lab.Compare(
            Measurement("Game", "fp", Frames(20.0d, 600, jitter: 2.0d)),
            Measurement("Game", "fp", Frames(16.7d, 600, jitter: 2.0d)));

        Assert.Equal(TrialDecision.Keep, comparison.Verdict.Decision);
        Assert.Contains(comparison.Verdict.Comparisons, metric => metric.MetricName == "p99_frame_time_ms");
    }

    [Fact]
    public void ADifferenceTooSmallToMatterIsNotSoldAsAWin()
    {
        // With 2000 frames a 0.2% change is statistically detectable. That is a fact about sample
        // size, not a reason to change someone's machine.
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource());

        BenchmarkComparison comparison = lab.Compare(
            Measurement("Game", "fp", Frames(16.700d, 2000, jitter: 0.05d)),
            Measurement("Game", "fp", Frames(16.667d, 2000, jitter: 0.05d)));

        Assert.NotEqual(TrialDecision.Keep, comparison.Verdict.Decision);
    }

    [Fact]
    public void AverageGainsThatCostFramePacingAreRejected()
    {
        BenchmarkLab lab = CreateLab(new UnavailableFrameTimeSource());

        // Slightly faster on average, badly worse in the tail: the change a naive tool calls a win.
        double[] baseline = Frames(16.7d, 1000, jitter: 0.4d);
        double[] candidate = Frames(16.2d, 1000, jitter: 0.4d);

        for (int index = 0; index < candidate.Length; index += 20)
        {
            candidate[index] = 42d;
        }

        BenchmarkComparison comparison = lab.Compare(
            Measurement("Game", "fp", baseline),
            Measurement("Game", "fp", candidate),
            ProfileKind.Competitive);

        Assert.Equal(TrialDecision.Revert, comparison.Verdict.Decision);
    }

    private BenchmarkLab CreateLab(IFrameTimeSource source) => new(
        source,
        new FrameTimeRecorder(NullLogger<FrameTimeRecorder>.Instance),
        _repository,
        new FakeSystemProfileProvider(
            MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore())),
        TimeProvider.System,
        NullLogger<BenchmarkLab>.Instance);

    private static BenchmarkRequest Request(string label) => new()
    {
        WorkloadId = "Test Game",
        Label = label,
        Duration = TimeSpan.FromMilliseconds(50),
    };

    private static BenchmarkMeasurement Measurement(
        string workload,
        string fingerprint,
        IReadOnlyList<double> frameTimes) => new(
        new BenchmarkRun
        {
            Id = Guid.NewGuid(),
            HardwareFingerprint = fingerprint,
            WorkloadId = workload,
            Label = "run",
            StartedAtUtc = DateTimeOffset.UnixEpoch,
        },
        frameTimes,
        new FrameCaptureStatus(FrameCaptureAvailability.Available, "ok"),
        new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Builds a deterministic frame time series around a target interval.</summary>
    private static double[] Frames(double meanMs, int count, double jitter = 1.0d)
    {
        // A fixed seed keeps the statistical assertions reproducible on every agent.
        var random = new Random(20260101);
        var frames = new double[count];

        for (int index = 0; index < count; index++)
        {
            frames[index] = meanMs + ((random.NextDouble() - 0.5d) * 2d * jitter);
        }

        return frames;
    }

    /// <summary>A frame source that replays a fixed series.</summary>
    private sealed class ScriptedFrameSource : IFrameTimeSource
    {
        private readonly IReadOnlyList<double> _intervals;

        public ScriptedFrameSource(IReadOnlyList<double> intervals) => _intervals = intervals;

        public Task<FrameCaptureStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FrameCaptureStatus(FrameCaptureAvailability.Available, "ok"));

        public async IAsyncEnumerable<FramePresentEvent> CaptureAsync(
            int processId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            double timestamp = 0d;

            foreach (double interval in _intervals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timestamp += interval;
                yield return new FramePresentEvent(processId, timestamp);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
