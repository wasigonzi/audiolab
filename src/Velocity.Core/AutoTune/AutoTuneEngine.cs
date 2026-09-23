using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Benchmarking;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Data.Repositories;

namespace Velocity.Core.AutoTune;

/// <summary>Measures candidate settings on this machine and keeps only the ones that help.</summary>
public interface IAutoTuneEngine
{
    /// <summary>Runs a tuning session.</summary>
    /// <param name="request">What to tune.</param>
    /// <param name="progress">Reports each candidate as it completes, for the UI.</param>
    /// <param name="cancellationToken">
    /// Cancelling stops the run and reverts the candidate in flight; candidates already kept stay
    /// kept, because they were measured.
    /// </param>
    /// <returns>The report.</returns>
    Task<AutoTuneReport> RunAsync(
        AutoTuneRequest request,
        IProgress<AutoTuneCandidateReport>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The auto-tune loop: measure, change one thing, measure again, keep it only if it helped.
/// </summary>
/// <remarks>
/// <para>
/// <b>One change at a time.</b> The engine applies exactly one candidate per measurement. Applying
/// several and measuring once produces a number that cannot be attributed to anything, which is how
/// every "optimizer" that claims twenty improvements arrives at a figure it cannot defend.
/// </para>
/// <para>
/// <b>Neutral is reverted.</b> A candidate is kept only when the evaluator returns
/// <see cref="TrialDecision.Keep"/>. A change that measures as no difference is undone, because a
/// modification to someone's operating system is a cost even when it is free in frames.
/// </para>
/// <para>
/// <b>The baseline is re-measured.</b> By default every candidate is judged against a baseline
/// taken immediately before it. A machine warms up, background work comes and goes, and the player
/// moves to a different part of the game; a baseline from twenty minutes ago quietly turns drift
/// into a result.
/// </para>
/// <para>
/// <b>Nothing is concluded without frames.</b> If capture is unavailable or a measurement returns
/// no frames, the candidate is reverted and recorded as not measured. The engine never falls back
/// to resource counters to decide whether a setting helped.
/// </para>
/// <para>
/// <b>Results never cross machines.</b> Every result is stored and read under this machine's
/// fingerprint and this workload. There is no path by which a result measured elsewhere can
/// influence what is applied here.
/// </para>
/// </remarks>
public sealed class AutoTuneEngine : IAutoTuneEngine
{
    private readonly IBenchmarkLab _lab;
    private readonly IOptimizationEngine _engine;
    private readonly IRollbackEngine _rollback;
    private readonly ITweakRegistry _registry;
    private readonly ITrialResultRepository _results;
    private readonly ISystemProfileProvider _profiles;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutoTuneEngine> _logger;

    /// <summary>Creates the engine.</summary>
    /// <param name="lab">Benchmark lab used for every measurement.</param>
    /// <param name="engine">Transaction coordinator.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="registry">Module catalogue, used to reject unknown candidates.</param>
    /// <param name="results">Trial result storage.</param>
    /// <param name="profiles">Supplies the machine fingerprint results are scoped to.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public AutoTuneEngine(
        IBenchmarkLab lab,
        IOptimizationEngine engine,
        IRollbackEngine rollback,
        ITweakRegistry registry,
        ITrialResultRepository results,
        ISystemProfileProvider profiles,
        TimeProvider timeProvider,
        ILogger<AutoTuneEngine> logger)
    {
        _lab = lab ?? throw new ArgumentNullException(nameof(lab));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AutoTuneReport> RunAsync(
        AutoTuneRequest request,
        IProgress<AutoTuneCandidateReport>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SystemProfile machine = await _profiles.GetAsync(cancellationToken).ConfigureAwait(false);
        string fingerprint = machine.Fingerprint.CompositeHash;
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        var reports = new List<AutoTuneCandidateReport>();
        string? stoppedBecause = null;

        FrameCaptureStatus capture = await _lab
            .GetCaptureStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!capture.CanCapture)
        {
            // Without frames there is nothing to decide on, so nothing is applied at all.
            _logger.LogWarning("Auto-tune cannot run: {Detail}", capture.Detail);

            return new AutoTuneReport
            {
                WorkloadId = request.WorkloadId,
                HardwareFingerprint = fingerprint,
                StartedAtUtc = startedAt,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
                Candidates = [.. request.Candidates.Select(candidate => new AutoTuneCandidateReport(
                    candidate, AutoTuneOutcome.NotMeasured, null, capture.Detail))],
                StoppedBecause = capture.Detail,
            };
        }

        BenchmarkMeasurement? baseline = null;

        foreach (AutoTuneCandidate candidate in request.Candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stoppedBecause = "The run was cancelled.";
                reports.AddRange(Remaining(request, reports, "The run was cancelled before this candidate."));
                break;
            }

            AutoTuneCandidateReport report = await TrialAsync(
                    request, candidate, fingerprint, baseline, cancellationToken)
                .ConfigureAwait(false);

            reports.Add(report);
            progress?.Report(report);

            // A kept change is the new normal, so the next candidate is measured against it rather
            // than against a machine that no longer exists.
            baseline = request.RebaselineBeforeEachCandidate ? null : baseline;
        }

        return new AutoTuneReport
        {
            WorkloadId = request.WorkloadId,
            HardwareFingerprint = fingerprint,
            StartedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            Candidates = reports,
            StoppedBecause = stoppedBecause,
        };
    }

    private async Task<AutoTuneCandidateReport> TrialAsync(
        AutoTuneRequest request,
        AutoTuneCandidate candidate,
        string fingerprint,
        BenchmarkMeasurement? existingBaseline,
        CancellationToken cancellationToken)
    {
        if (_registry.Find(candidate.TweakId) is null)
        {
            return new AutoTuneCandidateReport(
                candidate,
                AutoTuneOutcome.Incompatible,
                null,
                $"'{candidate.TweakId}' is not a module in this build.");
        }

        string optionsHash = candidate.OptionsHash();

        if (request.SkipAlreadyMeasured)
        {
            TweakTrialResult? previous = await _results
                .GetResultAsync(fingerprint, request.WorkloadId, candidate.TweakId, optionsHash, cancellationToken)
                .ConfigureAwait(false);

            if (previous is not null)
            {
                return new AutoTuneCandidateReport(
                    candidate,
                    AutoTuneOutcome.AlreadyMeasured,
                    previous,
                    $"Already measured on this machine: {previous.Rationale}");
            }
        }

        BenchmarkMeasurement baseline = existingBaseline ?? await _lab
            .MeasureAsync(
                new BenchmarkRequest
                {
                    WorkloadId = request.WorkloadId,
                    ProcessId = request.ProcessId,
                    Label = $"baseline before {candidate.TweakId}",
                    Duration = request.MeasurementDuration,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!baseline.HasFrameData)
        {
            return new AutoTuneCandidateReport(
                candidate,
                AutoTuneOutcome.NotMeasured,
                null,
                "The baseline measurement captured no frames, so nothing was changed.");
        }

        OptimizationRunResult applied = await _engine
            .ApplyAsync(
                new OptimizationRequest
                {
                    TweakIds = [candidate.TweakId],
                    Reason = TransactionReason.AutoTuneTrial,
                    Options = new Dictionary<string, IReadOnlyDictionary<string, string>>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [candidate.TweakId] = candidate.Options,
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        TweakRunResult? step = applied.Results.FirstOrDefault();

        if (step is null ||
            step.Outcome is ApplyOutcome.Skipped or ApplyOutcome.Failed or ApplyOutcome.NoChangeRequired)
        {
            return new AutoTuneCandidateReport(
                candidate,
                AutoTuneOutcome.Incompatible,
                null,
                step?.Message ?? "The module did not apply on this machine, so there was nothing to measure.");
        }

        if (step.Outcome == ApplyOutcome.AppliedPendingRestart)
        {
            // Measuring now would measure the machine as it still is. Claiming a verdict from that
            // would be worse than admitting the setting cannot be auto-tuned in one sitting.
            await RevertAsync(applied.TransactionId).ConfigureAwait(false);

            return new AutoTuneCandidateReport(
                candidate,
                AutoTuneOutcome.NotMeasured,
                null,
                "This setting only takes effect after a restart, so it cannot be measured in a " +
                "single tuning run. It was reverted; apply it manually and benchmark across a restart.");
        }

        BenchmarkMeasurement withCandidate = await _lab
            .MeasureAsync(
                new BenchmarkRequest
                {
                    WorkloadId = request.WorkloadId,
                    ProcessId = request.ProcessId,
                    Label = $"candidate {candidate.TweakId}",
                    Duration = request.MeasurementDuration,
                    TransactionId = applied.TransactionId,
                    AppliedTweakIds = [candidate.TweakId],
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!withCandidate.HasFrameData)
        {
            await RevertAsync(applied.TransactionId).ConfigureAwait(false);

            return new AutoTuneCandidateReport(
                candidate,
                AutoTuneOutcome.NotMeasured,
                null,
                "The measurement captured no frames, so no conclusion was drawn and the change was undone.");
        }

        BenchmarkComparison comparison = _lab.Compare(baseline, withCandidate, request.ProfileKind);

        TweakTrialResult result = BuildResult(
            request, candidate, fingerprint, optionsHash, baseline, withCandidate, comparison);

        await _results.UpsertAsync(result, cancellationToken).ConfigureAwait(false);

        if (comparison.Verdict.Decision == TrialDecision.Keep)
        {
            _logger.LogInformation(
                "Auto-tune kept {Tweak}: {Rationale}", candidate.TweakId, comparison.Verdict.Rationale);

            return new AutoTuneCandidateReport(
                candidate, AutoTuneOutcome.Kept, result, comparison.Verdict.Rationale);
        }

        // Neutral and worse are both reverted: an unnecessary change is a cost even at zero frames.
        await RevertAsync(applied.TransactionId).ConfigureAwait(false);

        return new AutoTuneCandidateReport(
            candidate, AutoTuneOutcome.Reverted, result, comparison.Verdict.Rationale);
    }

    private static TweakTrialResult BuildResult(
        AutoTuneRequest request,
        AutoTuneCandidate candidate,
        string fingerprint,
        string optionsHash,
        BenchmarkMeasurement baseline,
        BenchmarkMeasurement withCandidate,
        BenchmarkComparison comparison)
    {
        MetricComparison? decisive = comparison.Verdict.Comparisons
            .FirstOrDefault(metric => metric.MetricName == "p99_frame_time_ms")
            ?? comparison.Verdict.Comparisons.FirstOrDefault();

        return new TweakTrialResult
        {
            HardwareFingerprint = fingerprint,
            WorkloadId = request.WorkloadId,
            TweakId = candidate.TweakId,
            OptionsHash = optionsHash,
            Options = candidate.Options,
            Decision = comparison.Verdict.Decision,
            Rationale = comparison.Verdict.Rationale,
            BaselineRunId = baseline.Run?.Id,
            CandidateRunId = withCandidate.Run?.Id,
            BaselineP99Ms = baseline.Run?.FrameTimes?.P99FrameTimeMs,
            CandidateP99Ms = withCandidate.Run?.FrameTimes?.P99FrameTimeMs,
            BaselineMeanMs = baseline.Run?.FrameTimes?.MeanFrameTimeMs,
            CandidateMeanMs = withCandidate.Run?.FrameTimes?.MeanFrameTimeMs,
            RelativeChange = decisive?.RelativeChange,
            PValue = decisive?.PValue,
            SampleCount = withCandidate.FrameTimesMs.Count,
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task RevertAsync(Guid transactionId)
    {
        try
        {
            // The revert runs on a fresh token: a cancelled tuning run must still leave the machine
            // as it found it.
            RollbackResult result = await _rollback
                .RollbackTransactionAsync(transactionId, CancellationToken.None)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Auto-tune could not fully revert transaction {Transaction}: {Failures}",
                    transactionId,
                    string.Join("; ", result.Failures.Select(failure => $"{failure.Key}: {failure.Value}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-tune failed to revert transaction {Transaction}.", transactionId);
        }
    }

    private static IEnumerable<AutoTuneCandidateReport> Remaining(
        AutoTuneRequest request,
        IReadOnlyList<AutoTuneCandidateReport> done,
        string detail) =>
        request.Candidates
            .Skip(done.Count)
            .Select(candidate => new AutoTuneCandidateReport(
                candidate, AutoTuneOutcome.NotReached, null, detail));
}
