using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Network;
using Velocity.Core.Network;

namespace Velocity.Core.Tests;

/// <summary>
/// The network module's claims are only as good as this measurement, so the thresholds and the
/// jitter definition are pinned here.
/// </summary>
public sealed class NetworkQualityAnalyzerTests
{
    [Fact]
    public void JitterIsConsecutiveVariation_NotDeviationFromTheMean()
    {
        // Both series have the same mean and the same standard deviation, but the alternating one
        // is what a player feels.
        double[] alternating = [20, 60, 20, 60, 20, 60];
        double[] drifting = [20, 20, 20, 60, 60, 60];

        Assert.Equal(40d, NetworkQualityAnalyzer.ComputeJitter(alternating), 3);
        Assert.Equal(8d, NetworkQualityAnalyzer.ComputeJitter(drifting), 3);
    }

    [Fact]
    public void JitterOfASingleSampleIsZero()
    {
        Assert.Equal(0d, NetworkQualityAnalyzer.ComputeJitter([25d]));
        Assert.Equal(0d, NetworkQualityAnalyzer.ComputeJitter([]));
    }

    [Fact]
    public void ASteadyLowLatencyLink_IsExcellent()
    {
        NetworkQualityMeasurement result = Analyze(20, Enumerable.Repeat(12d, 20).ToArray());

        Assert.Equal(ConnectionQuality.Excellent, result.Quality);
        Assert.Equal(0d, result.PacketLoss);
        Assert.Contains("not what is limiting", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketLoss_OverridesEverythingElse()
    {
        // Latency and jitter are perfect; loss still makes it Poor, and the summary says why.
        NetworkQualityMeasurement result = Analyze(100, Enumerable.Repeat(10d, 90).ToArray());

        Assert.Equal(ConnectionQuality.Poor, result.Quality);
        Assert.Contains("No adapter setting fixes packet loss", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void HighJitter_IsPoorEvenWithALowAverage()
    {
        double[] spiky = Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? 8d : 48d).ToArray();

        NetworkQualityMeasurement result = Analyze(30, spiky);

        Assert.Equal(ConnectionQuality.Poor, result.Quality);
        Assert.Contains("Jitter", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void HighLatencyWithLowJitter_IsFairAndSaysNoLocalSettingHelps()
    {
        NetworkQualityMeasurement result = Analyze(20, Enumerable.Repeat(140d, 20).ToArray());

        Assert.Equal(ConnectionQuality.Fair, result.Quality);
        Assert.Contains("distance to the server", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReplies_IsReportedAsUnreachableRatherThanPerfect()
    {
        NetworkQualityMeasurement result = Analyze(10, []);

        Assert.Equal(ConnectionQuality.Poor, result.Quality);
        Assert.Equal(0, result.RepliesReceived);
        Assert.Equal(1d, result.PacketLoss);
    }

    [Fact]
    public void NoProbes_IsUnknownRatherThanPoor()
    {
        NetworkQualityMeasurement result = Analyze(0, []);

        Assert.Equal(ConnectionQuality.Unknown, result.Quality);
    }

    [Fact]
    public void PercentilesAndMinimumAreReported()
    {
        double[] samples = [10, 12, 14, 16, 18, 20, 22, 24, 26, 90];

        NetworkQualityMeasurement result = Analyze(10, samples);

        Assert.Equal(10d, result.MinimumRoundTripMs);
        Assert.Equal(19d, result.MedianRoundTripMs, 3);
        Assert.True(result.P95RoundTripMs > result.MedianRoundTripMs);
    }

    private static NetworkQualityMeasurement Analyze(int probesSent, IReadOnlyList<double> roundTrips) =>
        NetworkQualityAnalyzer.Analyze(
            "example.invalid", DateTimeOffset.UnixEpoch, probesSent, roundTrips);
}
