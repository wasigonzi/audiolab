using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Core.Sessions;
using Velocity.Core.Statistics;
using Velocity.Data.Repositories;

namespace Velocity.Core.Benchmarking;

/// <summary>What to measure.</summary>
public sealed record BenchmarkRequest
{
    /// <summary>Game or workload being measured; comparisons never cross this boundary.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>Process to capture frames from, or <c>0</c> for every process.</summary>
    public int ProcessId { get; init; }

    /// <summary>Label distinguishing this run, for example <c>baseline</c> or <c>candidate</c>.</summary>
    public required string Label { get; init; }

    /// <summary>How long to measure.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Transaction whose state is in effect, when there is one.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>Tweaks applied during this run, recorded with the result.</summary>
    public IReadOnlyList<string> AppliedTweakIds { get; init; } = Array.Empty<string>();
}

/// <summary>Result of one measurement.</summary>
/// <param name="Run">The stored run, when frames were captured.</param>
/// <param name="FrameTimesMs">The raw intervals, kept in memory for the comparison.</param>
/// <param name="Status">Whether frame capture was available, and why not when it was not.</param>
/// <param name="DiscardedFrames">Presents dropped during capture, by reason.</param>
public sealed record BenchmarkMeasurement(
    BenchmarkRun? Run,
    IReadOnlyList<double> FrameTimesMs,
    FrameCaptureStatus Status,
    IReadOnlyDictionary<string, int> DiscardedFrames)
{
    /// <summary>Whether usable frame data was captured.</summary>
    public bool HasFrameData => FrameTimesMs.Count > 0;
}

/// <summary>A before and after comparison.</summary>
/// <param name="Baseline">The before run.</param>
/// <param name="Candidate">The after run.</param>
/// <param name="Verdict">The statistical verdict and the per-metric comparisons.</param>
public sealed record BenchmarkComparison(
    BenchmarkMeasurement Baseline,
    BenchmarkMeasurement Candidate,
    TrialVerdict Verdict);

/// <summary>Measures a workload and compares two measurements of it.</summary>
public interface IBenchmarkLab
{
    /// <summary>Reports whether frame capture can run on this machine.</summary>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns>The status.</returns>
    Task<FrameCaptureStatus> GetCaptureStatusAsync(CancellationToken cancellationToken);

    /// <summary>Measures one run.</summary>
    /// <param name="request">What to measure.</param>
    /// <param name="cancellationToken">Token used to end the measurement early.</param>
    /// <returns>The measurement.</returns>
    Task<BenchmarkMeasurement> MeasureAsync(
        BenchmarkRequest request,
        CancellationToken cancellationToken);

    /// <summary>Compares two measurements of the same workload.</summary>
    /// <param name="baseline">The before measurement.</param>
    /// <param name="candidate">The after measurement.</param>
    /// <param name="profileKind">Profile intent, which decides which metrics dominate.</param>
    /// <returns>The comparison.</returns>
    /// <exception cref="ArgumentException">The two runs measured different workloads.</exception>
    BenchmarkComparison Compare(
        BenchmarkMeasurement baseline,
        BenchmarkMeasurement candidate,
        ProfileKind profileKind = ProfileKind.Balanced);
}

/// <summary>
/// The Benchmark Lab: measure, measure again, and say whether the difference is real.
/// </summary>
/// <remarks>
/// <para>
/// This is where the product's central claim is either earned or dropped. Everything else can be
/// argued about; a before and after measurement on the user's own machine, with a significance test
/// and a confidence interval, either shows a difference or it does not.
/// </para>
/// <para>
/// <b>Three rules keep the comparison honest.</b> Runs are only ever compared within one hardware
/// fingerprint and one workload, because a result measured elsewhere is not evidence about this
/// machine. A run with no frame data produces an inconclusive verdict rather than a comparison of
/// resource counters dressed up as frames. And a difference that is statistically detectable but
/// smaller than the profile's threshold is reported as no difference, because with enough frames
/// any change becomes "significant" and that is a fact about sample size, not about the machine.
/// </para>
/// <para>
/// Resource telemetry is recorded alongside the frames because it explains a result — background
/// CPU falling is why the tail improved — but it is never the basis of the verdict.
/// </para>
/// </remarks>
public sealed class BenchmarkLab : IBenchmarkLab
{
    private readonly IFrameTimeSource _frames;
    private readonly FrameTimeRecorder _recorder;
    private readonly IBenchmarkRepository _repository;
    private readonly ISystemProfileProvider _profileProvider;
    private readonly ISystemMonitor? _monitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BenchmarkLab> _logger;

    /// <summary>Creates the lab.</summary>
    /// <param name="frames">Frame time source.</param>
    /// <param name="recorder">Turns present events into intervals.</param>
    /// <param name="repository">Benchmark history.</param>
    /// <param name="profileProvider">Supplies the hardware fingerprint a run is scoped to.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="monitor">Telemetry monitor, when one is available.</param>
    public BenchmarkLab(
        IFrameTimeSource frames,
        FrameTimeRecorder recorder,
        IBenchmarkRepository repository,
        ISystemProfileProvider profileProvider,
        TimeProvider timeProvider,
        ILogger<BenchmarkLab> logger,
        ISystemMonitor? monitor = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitor = monitor;
    }

    /// <inheritdoc />
    public Task<FrameCaptureStatus> GetCaptureStatusAsync(CancellationToken cancellationToken) =>
        _frames.GetStatusAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<BenchmarkMeasurement> MeasureAsync(
        BenchmarkRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        FrameCaptureStatus status = await _frames.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!status.CanCapture)
        {
            // No invented numbers: the caller is told why, and the comparison stays inconclusive.
            _logger.LogWarning("Frame capture is unavailable: {Detail}", status.Detail);
            return new BenchmarkMeasurement(null, [], status, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        SystemProfile machine = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        var telemetry = new TelemetryAccumulator();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(request.Duration);

        FrameCapture capture;

        using (telemetry.Attach(_monitor))
        {
            capture = await _recorder
                .CaptureAsync(_frames, request.ProcessId, window.Token)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (capture.Count == 0)
        {
            _logger.LogWarning(
                "The capture produced no usable frames for {Workload}; nothing is stored.",
                request.WorkloadId);

            return new BenchmarkMeasurement(null, [], status, capture.DiscardedFrames);
        }

        var run = new BenchmarkRun
        {
            Id = Guid.NewGuid(),
            HardwareFingerprint = machine.Fingerprint.CompositeHash,
            WorkloadId = request.WorkloadId,
            TransactionId = request.TransactionId,
            Label = request.Label,
            StartedAtUtc = startedAt,
            FrameTimes = DescriptiveStatistics.FromFrameTimes(capture.FrameTimesMs),
            Resources = telemetry.Summarize() ?? new ResourceStatistics(),
            AppliedTweakIds = request.AppliedTweakIds,
        };

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Measured {Frames} frames of {Workload} as '{Label}'.",
            capture.Count,
            request.WorkloadId,
            request.Label);

        return new BenchmarkMeasurement(run, capture.FrameTimesMs, status, capture.DiscardedFrames);
    }

    /// <inheritdoc />
    public BenchmarkComparison Compare(
        BenchmarkMeasurement baseline,
        BenchmarkMeasurement candidate,
        ProfileKind profileKind = ProfileKind.Balanced)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (baseline.Run is not null && candidate.Run is not null)
        {
            // Comparing across machines or workloads would produce a number with no meaning, so it
            // is refused rather than computed.
            if (!string.Equals(
                    baseline.Run.WorkloadId, candidate.Run.WorkloadId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"'{baseline.Run.WorkloadId}' and '{candidate.Run.WorkloadId}' are different " +
                    "workloads and cannot be compared.",
                    nameof(candidate));
            }

            if (!string.Equals(
                    baseline.Run.HardwareFingerprint,
                    candidate.Run.HardwareFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "These runs were measured on different hardware and cannot be compared.",
                    nameof(candidate));
            }
        }

        if (!baseline.HasFrameData || !candidate.HasFrameData)
        {
            return new BenchmarkComparison(baseline, candidate, new TrialVerdict
            {
                Decision = TrialDecision.NeedsMoreData,
                Rationale = DescribeMissingData(baseline, candidate),
                Comparisons = [],
            });
        }

        TrialVerdict verdict = TrialEvaluator.Evaluate(
            baseline.FrameTimesMs,
            candidate.FrameTimesMs,
            new TrialCriteria { ProfileKind = profileKind });

        return new BenchmarkComparison(baseline, candidate, verdict);
    }

    private static string DescribeMissingData(
        BenchmarkMeasurement baseline,
        BenchmarkMeasurement candidate)
    {
        BenchmarkMeasurement missing = baseline.HasFrameData ? candidate : baseline;
        string which = baseline.HasFrameData ? "candidate" : "baseline";

        return missing.Status.CanCapture
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The {which} run captured no frames, so there is nothing to compare. Make sure the game was rendering for the whole measurement.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Frame capture is unavailable, so no comparison can be made: {missing.Status.Detail}");
    }
}
