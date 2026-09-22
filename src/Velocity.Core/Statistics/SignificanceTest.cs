using System;
using System.Collections.Generic;
using System.Linq;

namespace Velocity.Core.Statistics;

/// <summary>Result of a two sample significance test.</summary>
/// <param name="PValue">Two sided p-value.</param>
/// <param name="EffectSize">
/// Rank biserial correlation, from -1 to 1. Positive means the candidate sample tends to be larger.
/// </param>
/// <param name="SampleSizeA">Size of the baseline sample.</param>
/// <param name="SampleSizeB">Size of the candidate sample.</param>
public readonly record struct SignificanceTestResult(
    double PValue,
    double EffectSize,
    int SampleSizeA,
    int SampleSizeB);

/// <summary>
/// Non parametric comparison of two samples.
/// </summary>
/// <remarks>
/// <para>
/// Frame time distributions are skewed and heavy tailed, which is the whole reason the tail
/// percentiles matter. A t-test assumes neither of those away safely, so the Mann-Whitney U test
/// is used: it compares distributions by rank, needs no normality assumption, and is not dragged
/// around by a handful of 80 ms frames the way a mean difference test is.
/// </para>
/// <para>
/// The normal approximation with tie correction is used, which is accurate for the sample sizes a
/// benchmark produces (hundreds to tens of thousands of frames) and avoids computing exact
/// distributions.
/// </para>
/// </remarks>
public static class SignificanceTest
{
    /// <summary>Runs a two sided Mann-Whitney U test.</summary>
    /// <param name="baseline">Baseline sample.</param>
    /// <param name="candidate">Candidate sample.</param>
    /// <returns>The p-value and effect size.</returns>
    public static SignificanceTestResult MannWhitneyU(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        int n1 = baseline.Count;
        int n2 = candidate.Count;

        if (n1 == 0 || n2 == 0)
        {
            return new SignificanceTestResult(1d, 0d, n1, n2);
        }

        (double[] ranks, double tieCorrection) = RankAll(baseline, candidate);

        double rankSumBaseline = 0d;
        for (int i = 0; i < n1; i++)
        {
            rankSumBaseline += ranks[i];
        }

        double u1 = rankSumBaseline - (n1 * (n1 + 1) / 2d);
        double u2 = ((double)n1 * n2) - u1;
        double u = Math.Min(u1, u2);

        double meanU = (double)n1 * n2 / 2d;
        int n = n1 + n2;
        double varianceU = ((double)n1 * n2 / 12d) * (n + 1 - (tieCorrection / ((double)n * (n - 1))));

        if (varianceU <= 0d)
        {
            // Every observation is identical: there is nothing to detect.
            return new SignificanceTestResult(1d, 0d, n1, n2);
        }

        // Continuity correction keeps the approximation honest for smaller samples.
        double z = (Math.Abs(u - meanU) - 0.5d) / Math.Sqrt(varianceU);
        double pValue = 2d * (1d - NormalDistribution.Cdf(Math.Max(z, 0d)));
        pValue = Math.Clamp(pValue, 0d, 1d);

        // Rank biserial correlation, signed so that positive means the candidate ranks higher.
        double effectSize = (2d * u2 / ((double)n1 * n2)) - 1d;

        return new SignificanceTestResult(pValue, effectSize, n1, n2);
    }

    /// <summary>
    /// Estimates a percentile bootstrap confidence interval for the difference in means.
    /// </summary>
    /// <param name="baseline">Baseline sample.</param>
    /// <param name="candidate">Candidate sample.</param>
    /// <param name="confidenceLevel">Confidence level, for example 0.95.</param>
    /// <param name="resamples">Number of bootstrap resamples.</param>
    /// <param name="seed">
    /// Seed for the resampling generator. Fixed by default so that the same measurements always
    /// produce the same verdict; a decision that changes between runs on identical data is not a
    /// decision a user can trust.
    /// </param>
    /// <returns>The interval bounds, candidate minus baseline.</returns>
    public static (double Low, double High) BootstrapMeanDifferenceInterval(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
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

        for (int i = 0; i < resamples; i++)
        {
            differences[i] = ResampleMean(candidate, random) - ResampleMean(baseline, random);
        }

        Array.Sort(differences);

        double alpha = (1d - confidenceLevel) / 2d;
        return (
            DescriptiveStatistics.Percentile(differences, alpha * 100d),
            DescriptiveStatistics.Percentile(differences, (1d - alpha) * 100d));
    }

    private static double ResampleMean(IReadOnlyList<double> sample, Random random)
    {
        double sum = 0d;
        for (int i = 0; i < sample.Count; i++)
        {
            sum += sample[random.Next(sample.Count)];
        }

        return sum / sample.Count;
    }

    private static (double[] Ranks, double TieCorrection) RankAll(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        int n = first.Count + second.Count;
        var indexed = new (double Value, int Index)[n];

        for (int i = 0; i < first.Count; i++)
        {
            indexed[i] = (first[i], i);
        }

        for (int i = 0; i < second.Count; i++)
        {
            indexed[first.Count + i] = (second[i], first.Count + i);
        }

        Array.Sort(indexed, (a, b) => a.Value.CompareTo(b.Value));

        double[] ranks = new double[n];
        double tieCorrection = 0d;
        int position = 0;

        while (position < n)
        {
            int runEnd = position;
            while (runEnd + 1 < n && indexed[runEnd + 1].Value.Equals(indexed[position].Value))
            {
                runEnd++;
            }

            int runLength = runEnd - position + 1;
            double averageRank = ((position + 1) + (runEnd + 1)) / 2d;

            for (int i = position; i <= runEnd; i++)
            {
                ranks[indexed[i].Index] = averageRank;
            }

            if (runLength > 1)
            {
                tieCorrection += ((double)runLength * runLength * runLength) - runLength;
            }

            position = runEnd + 1;
        }

        return (ranks, tieCorrection);
    }
}

/// <summary>Standard normal distribution helpers.</summary>
internal static class NormalDistribution
{
    /// <summary>Cumulative distribution function of the standard normal distribution.</summary>
    /// <param name="z">Standard score.</param>
    /// <returns>The probability that a standard normal variate is at most <paramref name="z"/>.</returns>
    internal static double Cdf(double z) => 0.5d * (1d + Erf(z / Math.Sqrt(2d)));

    /// <summary>
    /// Error function, using the Abramowitz and Stegun 7.1.26 rational approximation
    /// (absolute error below 1.5e-7, far tighter than the precision a p-value threshold needs).
    /// </summary>
    /// <param name="x">Input value.</param>
    /// <returns>The error function value.</returns>
    private static double Erf(double x)
    {
        const double A1 = 0.254829592d;
        const double A2 = -0.284496736d;
        const double A3 = 1.421413741d;
        const double A4 = -1.453152027d;
        const double A5 = 1.061405429d;
        const double P = 0.3275911d;

        double sign = x < 0d ? -1d : 1d;
        double absolute = Math.Abs(x);

        double t = 1d / (1d + (P * absolute));
        double y = 1d - ((((((((A5 * t) + A4) * t) + A3) * t) + A2) * t + A1) * t * Math.Exp(-absolute * absolute));

        return sign * y;
    }
}
