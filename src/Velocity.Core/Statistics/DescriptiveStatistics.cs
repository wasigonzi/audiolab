using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Statistics;

/// <summary>Summary statistics over a sample.</summary>
public static class DescriptiveStatistics
{
    /// <summary>
    /// Returns the value at a percentile using linear interpolation between order statistics.
    /// </summary>
    /// <param name="sortedValues">Values in ascending order.</param>
    /// <param name="percentile">Percentile in the range 0 to 100.</param>
    /// <returns>The interpolated value.</returns>
    /// <remarks>
    /// Interpolation matters at the tail: with 600 frames, a nearest rank P99.9 can only ever
    /// return the single worst frame, which makes the 0.1% low figure a function of sample size
    /// rather than of the machine.
    /// </remarks>
    public static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100d);

        if (sortedValues.Count == 0)
        {
            return 0d;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        double rank = percentile / 100d * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);

        return lower == upper
            ? sortedValues[lower]
            : sortedValues[lower] + ((rank - lower) * (sortedValues[upper] - sortedValues[lower]));
    }

    /// <summary>Computes the arithmetic mean.</summary>
    /// <param name="values">Sample values.</param>
    /// <returns>The mean, or zero for an empty sample.</returns>
    public static double Mean(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return 0d;
        }

        double sum = 0d;
        for (int i = 0; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / values.Count;
    }

    /// <summary>Computes the sample standard deviation (Bessel corrected).</summary>
    /// <param name="values">Sample values.</param>
    /// <returns>The standard deviation, or zero for samples smaller than two.</returns>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return 0d;
        }

        double mean = Mean(values);
        double sumOfSquares = 0d;

        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            sumOfSquares += delta * delta;
        }

        return Math.Sqrt(sumOfSquares / (values.Count - 1));
    }

    /// <summary>
    /// Builds frame pacing statistics from a series of frame times.
    /// </summary>
    /// <param name="frameTimesMs">Frame times in milliseconds, in capture order.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentException">The sample is empty.</exception>
    public static FrameTimeStatistics FromFrameTimes(IReadOnlyList<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);

        if (frameTimesMs.Count == 0)
        {
            throw new ArgumentException("At least one frame time is required.", nameof(frameTimesMs));
        }

        double[] sorted = frameTimesMs.ToArray();
        Array.Sort(sorted);

        double total = 0d;
        for (int i = 0; i < frameTimesMs.Count; i++)
        {
            total += frameTimesMs[i];
        }

        return new FrameTimeStatistics
        {
            SampleCount = frameTimesMs.Count,
            Duration = TimeSpan.FromMilliseconds(total),
            MeanFrameTimeMs = Mean(frameTimesMs),
            MedianFrameTimeMs = Percentile(sorted, 50d),
            P95FrameTimeMs = Percentile(sorted, 95d),
            P99FrameTimeMs = Percentile(sorted, 99d),
            P999FrameTimeMs = Percentile(sorted, 99.9d),
            StandardDeviationMs = StandardDeviation(frameTimesMs),
        };
    }
}
