using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Core.Statistics;

namespace Velocity.Core.Tests;

/// <summary>Tests for the measurement maths the auto-tune engine will depend on.</summary>
public sealed class DescriptiveStatisticsTests
{
    [Fact]
    public void Percentile_InterpolatesBetweenOrderStatistics()
    {
        double[] values = [1, 2, 3, 4];

        Assert.Equal(2.5d, DescriptiveStatistics.Percentile(values, 50d), 6);
    }

    [Fact]
    public void Percentile_HandlesTheBounds()
    {
        double[] values = [5, 10, 15];

        Assert.Equal(5d, DescriptiveStatistics.Percentile(values, 0d));
        Assert.Equal(15d, DescriptiveStatistics.Percentile(values, 100d));
    }

    [Fact]
    public void Percentile_OfASingleSampleIsThatSample()
    {
        Assert.Equal(42d, DescriptiveStatistics.Percentile([42d], 99.9d));
    }

    [Fact]
    public void StandardDeviation_UsesTheSampleFormula()
    {
        double[] values = [2, 4, 4, 4, 5, 5, 7, 9];

        // Population sigma is 2; the Bessel corrected sample value is slightly larger.
        Assert.Equal(2.138d, DescriptiveStatistics.StandardDeviation(values), 3);
    }

    [Fact]
    public void FrameTimeStatistics_DeriveFrameRatesFromTheTailPercentiles()
    {
        // 100 frames at 10 ms, with the last one at 50 ms.
        var frames = Enumerable.Repeat(10d, 99).Append(50d).ToArray();

        FrameTimeStatistics statistics = DescriptiveStatistics.FromFrameTimes(frames);

        Assert.Equal(100, statistics.SampleCount);
        Assert.Equal(10d, statistics.MedianFrameTimeMs, 3);
        Assert.True(statistics.P999FrameTimeMs > statistics.P95FrameTimeMs);
        Assert.True(statistics.AverageFps > statistics.OnePercentLowFps);
    }

    [Fact]
    public void FrameTimeStatistics_RejectAnEmptySample()
    {
        Assert.Throws<ArgumentException>(() => DescriptiveStatistics.FromFrameTimes(Array.Empty<double>()));
    }
}

/// <summary>Tests for the significance test used to decide whether a difference is real.</summary>
public sealed class SignificanceTestTests
{
    [Fact]
    public void IdenticalSamples_AreNotSignificant()
    {
        double[] sample = Enumerable.Repeat(10d, 500).ToArray();

        SignificanceTestResult result = SignificanceTest.MannWhitneyU(sample, sample);

        Assert.Equal(1d, result.PValue, 6);
    }

    [Fact]
    public void ClearlySeparatedSamples_AreSignificant()
    {
        double[] baseline = Enumerable.Range(0, 200).Select(i => 16d + (i % 5 * 0.1d)).ToArray();
        double[] candidate = Enumerable.Range(0, 200).Select(i => 12d + (i % 5 * 0.1d)).ToArray();

        SignificanceTestResult result = SignificanceTest.MannWhitneyU(baseline, candidate);

        Assert.True(result.PValue < 0.001d, $"p-value was {result.PValue}");
    }

    [Fact]
    public void NoiseWithoutASystematicShift_IsNotSignificant()
    {
        var random = new Random(1234);
        double[] baseline = Enumerable.Range(0, 400).Select(_ => 16d + (random.NextDouble() - 0.5d)).ToArray();
        double[] candidate = Enumerable.Range(0, 400).Select(_ => 16d + (random.NextDouble() - 0.5d)).ToArray();

        SignificanceTestResult result = SignificanceTest.MannWhitneyU(baseline, candidate);

        Assert.True(result.PValue > 0.05d, $"p-value was {result.PValue}");
    }

    [Fact]
    public void EmptySamples_ReportNoDifferenceRatherThanThrowing()
    {
        SignificanceTestResult result = SignificanceTest.MannWhitneyU(Array.Empty<double>(), [1d, 2d]);

        Assert.Equal(1d, result.PValue);
    }

    [Fact]
    public void BootstrapInterval_ExcludesZeroForARealDifference()
    {
        double[] baseline = Enumerable.Repeat(16d, 300).ToArray();
        double[] candidate = Enumerable.Repeat(12d, 300).ToArray();

        (double low, double high) =
            SignificanceTest.BootstrapMeanDifferenceInterval(baseline, candidate);

        Assert.True(high < 0d, $"interval was [{low}, {high}]");
    }

    [Fact]
    public void BootstrapInterval_IsReproducible()
    {
        var random = new Random(7);
        double[] baseline = Enumerable.Range(0, 300).Select(_ => 16d + random.NextDouble()).ToArray();
        double[] candidate = Enumerable.Range(0, 300).Select(_ => 15.9d + random.NextDouble()).ToArray();

        (double firstLow, double firstHigh) =
            SignificanceTest.BootstrapMeanDifferenceInterval(baseline, candidate);
        (double secondLow, double secondHigh) =
            SignificanceTest.BootstrapMeanDifferenceInterval(baseline, candidate);

        // The same measurements must always produce the same verdict.
        Assert.Equal(firstLow, secondLow);
        Assert.Equal(firstHigh, secondHigh);
    }
}

/// <summary>
/// Tests for the keep-or-revert decision, including the case the product exists to get right:
/// an average that improves while the tail gets worse.
/// </summary>
public sealed class TrialEvaluatorTests
{
    [Fact]
    public void NeedsMoreData_WhenTheSamplesAreTooSmall()
    {
        TrialVerdict verdict = TrialEvaluator.Evaluate(
            Enumerable.Repeat(16d, 50).ToArray(),
            Enumerable.Repeat(15d, 50).ToArray());

        Assert.Equal(TrialDecision.NeedsMoreData, verdict.Decision);
    }

    [Fact]
    public void Keep_WhenEveryMetricImproves()
    {
        double[] baseline = Frames(seed: 1, centre: 16d, spikeMs: 40d, spikeEvery: 50);
        double[] candidate = Frames(seed: 2, centre: 13d, spikeMs: 30d, spikeEvery: 50);

        TrialVerdict verdict = TrialEvaluator.Evaluate(baseline, candidate);

        Assert.Equal(TrialDecision.Keep, verdict.Decision);
    }

    [Fact]
    public void Revert_WhenTheAverageImprovesButTheTailGetsWorse()
    {
        // Mean frame time drops, which would read as "average FPS up", but the worst frames get
        // much worse. This is exactly the trade the product refuses to call a win.
        double[] baseline = Frames(seed: 3, centre: 16d, spikeMs: 20d, spikeEvery: 25);
        double[] candidate = Frames(seed: 4, centre: 15d, spikeMs: 90d, spikeEvery: 25);

        TrialVerdict verdict = TrialEvaluator.Evaluate(baseline, candidate);

        Assert.Equal(TrialDecision.Revert, verdict.Decision);
        Assert.Contains("stutter regression", verdict.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_WhenNothingChangesMeaningfully()
    {
        double[] baseline = Frames(seed: 5, centre: 16d, spikeMs: 25d, spikeEvery: 40);
        double[] candidate = Frames(seed: 6, centre: 16d, spikeMs: 25d, spikeEvery: 40);

        TrialVerdict verdict = TrialEvaluator.Evaluate(baseline, candidate);

        Assert.Equal(TrialDecision.Revert, verdict.Decision);
        Assert.Contains("No credible improvement", verdict.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void CompetitiveProfile_RejectsAnImprovementThatMissesTheTail()
    {
        TrialVerdict verdict = TrialEvaluator.Evaluate(
            UpperMiddleImprovement(slowFrameMs: 20d),
            UpperMiddleImprovement(slowFrameMs: 16.2d),
            new TrialCriteria { ProfileKind = ProfileKind.Competitive });

        // The median and both tail percentiles are identical in the two runs; only the band
        // between them moved. A competitive profile has no reason to keep that.
        Assert.Equal(TrialDecision.Revert, verdict.Decision);
        Assert.Contains("No credible improvement", verdict.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumFpsProfile_KeepsTheSameChangeBecauseTheMeanImproved()
    {
        TrialVerdict verdict = TrialEvaluator.Evaluate(
            UpperMiddleImprovement(slowFrameMs: 20d),
            UpperMiddleImprovement(slowFrameMs: 16.2d),
            new TrialCriteria { ProfileKind = ProfileKind.MaximumFps });

        Assert.Equal(TrialDecision.Keep, verdict.Decision);
    }

    /// <summary>
    /// Builds a run whose median and tail percentiles are fixed, and where only the band between
    /// the median and P95 varies. Seventy per cent of frames are fast, two per cent are identical
    /// stutters, and the remainder are the frames under test.
    /// </summary>
    private static double[] UpperMiddleImprovement(double slowFrameMs)
    {
        var frames = new double[1000];

        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = i < 700 ? 16d
                : i < 980 ? slowFrameMs
                : 60d;
        }

        return frames;
    }

    [Fact]
    public void EveryComparisonIsReportedSoTheUserCanCheckTheVerdict()
    {
        double[] baseline = Frames(seed: 7, centre: 16d, spikeMs: 30d, spikeEvery: 40);
        double[] candidate = Frames(seed: 8, centre: 14d, spikeMs: 28d, spikeEvery: 40);

        TrialVerdict verdict = TrialEvaluator.Evaluate(baseline, candidate);

        Assert.Equal(5, verdict.Comparisons.Count);
        Assert.All(verdict.Comparisons, comparison =>
            Assert.Equal(MetricPolarity.LowerIsBetter, comparison.Polarity));
        Assert.All(verdict.Comparisons, comparison => Assert.NotNull(comparison.ConfidenceIntervalLow));
    }

    /// <summary>Builds a frame time series with realistic jitter and periodic spikes.</summary>
    private static double[] Frames(int seed, double centre, double spikeMs, int spikeEvery)
    {
        var random = new Random(seed);
        var frames = new double[900];

        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = i % spikeEvery == 0
                ? spikeMs + (random.NextDouble() * 2d)
                : centre + ((random.NextDouble() - 0.5d) * 1.5d);
        }

        return frames;
    }
}
