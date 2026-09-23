using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Statistics;

/// <summary>Thresholds the auto-tune engine applies when judging a candidate change.</summary>
public sealed record TrialCriteria
{
    /// <summary>Profile intent, which decides which metrics dominate the verdict.</summary>
    public ProfileKind ProfileKind { get; init; } = ProfileKind.Balanced;

    /// <summary>Minimum number of frames each sample must contain before a verdict is given.</summary>
    public int MinimumSamples { get; init; } = 300;

    /// <summary>
    /// Relative change below which a difference is treated as not worth keeping, even when it is
    /// statistically detectable. With enough frames, a 0.2% difference becomes "significant"; that
    /// is a fact about sample size, not a reason to change someone's machine.
    /// </summary>
    public double MinimumRelativeImprovement { get; init; } = 0.01d;

    /// <summary>
    /// Relative worsening of a tail metric that vetoes the change regardless of what the averages
    /// did. This is the rule that stops "average FPS up 1%, P99 frame time up 15%" from being
    /// reported as a win.
    /// </summary>
    public double TailRegressionVeto { get; init; } = 0.02d;

    /// <summary>Confidence level for the bootstrap intervals.</summary>
    public double ConfidenceLevel { get; init; } = 0.95d;

    /// <summary>Number of bootstrap resamples per metric.</summary>
    public int BootstrapResamples { get; init; } = 2000;
}

/// <summary>
/// Decides whether a candidate configuration is kept or reverted.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator works on frame time samples rather than on summary numbers, so it can put a
/// confidence interval around each percentile instead of comparing two point estimates and calling
/// the larger one better.
/// </para>
/// <para>
/// Its bias is deliberately conservative: a change is kept only when it produces a credible
/// improvement in the metric the profile cares about <em>and</em> does not credibly worsen the
/// tail. A change that measures as neutral is reverted, because an unnecessary modification to
/// someone's operating system is a cost even when it is free in frames.
/// </para>
/// </remarks>
public static class TrialEvaluator
{
    private const string MeanMetric = "mean_frame_time_ms";
    private const string MedianMetric = "median_frame_time_ms";
    private const string P95Metric = "p95_frame_time_ms";
    private const string P99Metric = "p99_frame_time_ms";
    private const string P999Metric = "p999_frame_time_ms";

    private static readonly string[] TailMetrics = [P95Metric, P99Metric, P999Metric];

    /// <summary>Judges a candidate frame time sample against a baseline.</summary>
    /// <param name="baselineFrameTimesMs">Baseline frame times in milliseconds.</param>
    /// <param name="candidateFrameTimesMs">Candidate frame times in milliseconds.</param>
    /// <param name="criteria">Thresholds to apply.</param>
    /// <returns>The verdict and the comparisons behind it.</returns>
    public static TrialVerdict Evaluate(
        IReadOnlyList<double> baselineFrameTimesMs,
        IReadOnlyList<double> candidateFrameTimesMs,
        TrialCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(baselineFrameTimesMs);
        ArgumentNullException.ThrowIfNull(candidateFrameTimesMs);

        criteria ??= new TrialCriteria();

        if (baselineFrameTimesMs.Count < criteria.MinimumSamples ||
            candidateFrameTimesMs.Count < criteria.MinimumSamples)
        {
            return new TrialVerdict
            {
                Decision = TrialDecision.NeedsMoreData,
                Rationale = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Each run needs at least {criteria.MinimumSamples} frames; captured {baselineFrameTimesMs.Count} and {candidateFrameTimesMs.Count}."),
                Comparisons = Array.Empty<MetricComparison>(),
            };
        }

        var comparisons = new List<MetricComparison>
        {
            Compare(MeanMetric, baselineFrameTimesMs, candidateFrameTimesMs, percentile: null, criteria),
            Compare(MedianMetric, baselineFrameTimesMs, candidateFrameTimesMs, 50d, criteria),
            Compare(P95Metric, baselineFrameTimesMs, candidateFrameTimesMs, 95d, criteria),
            Compare(P99Metric, baselineFrameTimesMs, candidateFrameTimesMs, 99d, criteria),
            Compare(P999Metric, baselineFrameTimesMs, candidateFrameTimesMs, 99.9d, criteria),
        };

        return Decide(comparisons, criteria);
    }

    private static TrialVerdict Decide(IReadOnlyList<MetricComparison> comparisons, TrialCriteria criteria)
    {
        MetricComparison? tailRegression = comparisons
            .Where(comparison => TailMetrics.Contains(comparison.MetricName))
            .Where(comparison => comparison.Verdict == SignificanceVerdict.Regression)
            .Where(comparison => comparison.RelativeChange >= criteria.TailRegressionVeto)
            .OrderByDescending(comparison => comparison.RelativeChange)
            .Cast<MetricComparison?>()
            .FirstOrDefault();

        if (tailRegression is not null)
        {
            return new TrialVerdict
            {
                Decision = TrialDecision.Revert,
                Rationale = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tailRegression.MetricName} worsened by {tailRegression.RelativeChange:P1}, which is a stutter regression. Changes that trade frame pacing for average frame rate are reverted."),
                Comparisons = comparisons,
            };
        }

        string[] decisiveMetrics = criteria.ProfileKind switch
        {
            // Competitive players feel the tail, so the tail is what has to improve.
            ProfileKind.Competitive => [P99Metric, P999Metric, MedianMetric],
            ProfileKind.MaximumFps => [MeanMetric, MedianMetric],
            _ => [MeanMetric, MedianMetric, P99Metric],
        };

        List<MetricComparison> improvements = comparisons
            .Where(comparison => decisiveMetrics.Contains(comparison.MetricName))
            .Where(comparison => comparison.Verdict == SignificanceVerdict.Improvement)
            .ToList();

        if (improvements.Count == 0)
        {
            return new TrialVerdict
            {
                Decision = TrialDecision.Revert,
                Rationale =
                    "No credible improvement was measured in the metrics this profile optimises for. " +
                    "The change is reverted rather than left in place on the chance that it helps.",
                Comparisons = comparisons,
            };
        }

        MetricComparison best = improvements
            .OrderBy(comparison => comparison.RelativeChange)
            .First();

        return new TrialVerdict
        {
            Decision = TrialDecision.Keep,
            Rationale = string.Create(
                CultureInfo.InvariantCulture,
                $"{best.MetricName} improved by {Math.Abs(best.RelativeChange):P1} with no tail regression."),
            Comparisons = comparisons,
        };
    }

    private static MetricComparison Compare(
        string metricName,
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        double? percentile,
        TrialCriteria criteria)
    {
        double baselineValue = Summarise(baseline, percentile);
        double candidateValue = Summarise(candidate, percentile);

        (double low, double high) = Bootstrap(baseline, candidate, percentile, criteria);

        // Frame time: lower is better, so a negative difference is an improvement.
        double relativeChange = Math.Abs(baselineValue) < double.Epsilon
            ? 0d
            : (candidateValue - baselineValue) / baselineValue;

        bool intervalExcludesZero = (low > 0d && high > 0d) || (low < 0d && high < 0d);
        bool largeEnough = Math.Abs(relativeChange) >= criteria.MinimumRelativeImprovement;

        SignificanceVerdict verdict = !intervalExcludesZero || !largeEnough
            ? SignificanceVerdict.NoDifference
            : candidateValue < baselineValue
                ? SignificanceVerdict.Improvement
                : SignificanceVerdict.Regression;

        double? pValue = percentile is null
            ? SignificanceTest.MannWhitneyU(baseline, candidate).PValue
            : null;

        return new MetricComparison
        {
            MetricName = metricName,
            Polarity = MetricPolarity.LowerIsBetter,
            BaselineValue = baselineValue,
            CandidateValue = candidateValue,
            PValue = pValue,
            ConfidenceIntervalLow = low,
            ConfidenceIntervalHigh = high,
            Verdict = verdict,
        };
    }

    private static double Summarise(IReadOnlyList<double> values, double? percentile)
    {
        if (percentile is null)
        {
            return DescriptiveStatistics.Mean(values);
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return DescriptiveStatistics.Percentile(sorted, percentile.Value);
    }

    private static (double Low, double High) Bootstrap(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        double? percentile,
        TrialCriteria criteria)
    {
        if (percentile is null)
        {
            return SignificanceTest.BootstrapMeanDifferenceInterval(
                baseline, candidate, criteria.ConfidenceLevel, criteria.BootstrapResamples);
        }

        return BootstrapPercentileDifferenceInterval(
            baseline, candidate, percentile.Value, criteria.ConfidenceLevel, criteria.BootstrapResamples);
    }

    /// <summary>
    /// Estimates a percentile bootstrap confidence interval for the difference between the same
    /// percentile of two samples.
    /// </summary>
    /// <param name="baseline">Baseline sample.</param>
    /// <param name="candidate">Candidate sample.</param>
    /// <param name="percentile">Percentile to compare, from 0 to 100.</param>
    /// <param name="confidenceLevel">Confidence level, for example 0.95.</param>
    /// <param name="resamples">Number of bootstrap resamples.</param>
    /// <param name="seed">Seed, fixed so that a verdict is reproducible from the same data.</param>
    /// <returns>The interval bounds, candidate minus baseline.</returns>
    public static (double Low, double High) BootstrapPercentileDifferenceInterval(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        double percentile,
        double confidenceLevel = 0.95d,
        int resamples = 2000,
        int seed = 20240101)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resamples);

        if (baseline.Count == 0 || candidate.Count == 0)
        {
            return (0d, 0d);
        }

        var random = new Random(seed);
        double[] differences = new double[resamples];
        double[] baselineBuffer = new double[baseline.Count];
        double[] candidateBuffer = new double[candidate.Count];

        for (int i = 0; i < resamples; i++)
        {
            differences[i] =
                ResamplePercentile(candidate, candidateBuffer, percentile, random) -
                ResamplePercentile(baseline, baselineBuffer, percentile, random);
        }

        Array.Sort(differences);
        double alpha = (1d - confidenceLevel) / 2d;

        return (
            DescriptiveStatistics.Percentile(differences, alpha * 100d),
            DescriptiveStatistics.Percentile(differences, (1d - alpha) * 100d));
    }

    private static double ResamplePercentile(
        IReadOnlyList<double> sample,
        double[] buffer,
        double percentile,
        Random random)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = sample[random.Next(sample.Count)];
        }

        Array.Sort(buffer);
        return DescriptiveStatistics.Percentile(buffer, percentile);
    }
}
