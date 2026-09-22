using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Network;

namespace Velocity.Core.Network;

/// <summary>
/// Turns round trip samples into the measurement the network module reports.
/// </summary>
/// <remarks>
/// Pure, so the thresholds and the jitter definition are tested rather than asserted. The
/// thresholds below are stated as what they are: a rule of thumb for how a connection feels in a
/// real time game, not a measurement of anything.
/// </remarks>
public static class NetworkQualityAnalyzer
{
    /// <summary>Round trip time at or below which latency is not the limiting factor.</summary>
    public const double ExcellentLatencyMs = 30d;

    /// <summary>Round trip time above which latency is felt in a competitive game.</summary>
    public const double PoorLatencyMs = 80d;

    /// <summary>Jitter at or below which frame to frame variation is not felt.</summary>
    public const double ExcellentJitterMs = 3d;

    /// <summary>Jitter above which play is visibly affected regardless of average latency.</summary>
    public const double PoorJitterMs = 15d;

    /// <summary>Packet loss above which no configuration change will help.</summary>
    public const double PoorPacketLoss = 0.02d;

    /// <summary>Builds a measurement from round trip samples.</summary>
    /// <param name="endpoint">Endpoint that was probed.</param>
    /// <param name="startedAtUtc">When the measurement started.</param>
    /// <param name="probesSent">Number of probes sent.</param>
    /// <param name="roundTripsMs">Round trip times of the replies that arrived, in order.</param>
    /// <returns>The measurement.</returns>
    public static NetworkQualityMeasurement Analyze(
        string endpoint,
        DateTimeOffset startedAtUtc,
        int probesSent,
        IReadOnlyList<double> roundTripsMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(roundTripsMs);

        if (roundTripsMs.Count == 0)
        {
            return new NetworkQualityMeasurement
            {
                Endpoint = endpoint,
                StartedAtUtc = startedAtUtc,
                ProbesSent = probesSent,
                RepliesReceived = 0,
                Quality = probesSent == 0 ? ConnectionQuality.Unknown : ConnectionQuality.Poor,
                Summary = probesSent == 0
                    ? "No probes were sent."
                    : "No replies were received, so the endpoint is unreachable or blocking probes.",
            };
        }

        double[] sorted = roundTripsMs.ToArray();
        Array.Sort(sorted);

        double mean = roundTripsMs.Average();
        double jitter = ComputeJitter(roundTripsMs);
        double loss = probesSent == 0 ? 0d : 1d - ((double)roundTripsMs.Count / probesSent);

        (ConnectionQuality quality, string summary) = Assess(mean, jitter, loss);

        return new NetworkQualityMeasurement
        {
            Endpoint = endpoint,
            StartedAtUtc = startedAtUtc,
            ProbesSent = probesSent,
            RepliesReceived = roundTripsMs.Count,
            MeanRoundTripMs = mean,
            MedianRoundTripMs = Percentile(sorted, 50d),
            MinimumRoundTripMs = sorted[0],
            P95RoundTripMs = Percentile(sorted, 95d),
            JitterMs = jitter,
            Quality = quality,
            Summary = summary,
        };
    }

    /// <summary>
    /// Mean absolute difference between consecutive samples.
    /// </summary>
    /// <param name="roundTripsMs">Samples in the order they arrived.</param>
    /// <returns>Jitter in milliseconds.</returns>
    public static double ComputeJitter(IReadOnlyList<double> roundTripsMs)
    {
        ArgumentNullException.ThrowIfNull(roundTripsMs);

        if (roundTripsMs.Count < 2)
        {
            return 0d;
        }

        double total = 0d;
        for (int i = 1; i < roundTripsMs.Count; i++)
        {
            total += Math.Abs(roundTripsMs[i] - roundTripsMs[i - 1]);
        }

        return total / (roundTripsMs.Count - 1);
    }

    private static (ConnectionQuality Quality, string Summary) Assess(
        double meanMs,
        double jitterMs,
        double loss)
    {
        if (loss > PoorPacketLoss)
        {
            return (ConnectionQuality.Poor, string.Create(
                CultureInfo.InvariantCulture,
                $"{loss:P1} of probes were lost. No adapter setting fixes packet loss; this is a link or route problem."));
        }

        if (jitterMs > PoorJitterMs)
        {
            return (ConnectionQuality.Poor, string.Create(
                CultureInfo.InvariantCulture,
                $"Jitter is {jitterMs:0.0} ms, which is felt in play regardless of the average latency."));
        }

        if (meanMs > PoorLatencyMs)
        {
            return (ConnectionQuality.Fair, string.Create(
                CultureInfo.InvariantCulture,
                $"Average round trip is {meanMs:0} ms. That is distance to the server, and no local setting reduces it."));
        }

        if (meanMs <= ExcellentLatencyMs && jitterMs <= ExcellentJitterMs)
        {
            return (ConnectionQuality.Excellent, string.Create(
                CultureInfo.InvariantCulture,
                $"{meanMs:0} ms average with {jitterMs:0.0} ms jitter. Latency is not what is limiting this machine."));
        }

        return (ConnectionQuality.Good, string.Create(
            CultureInfo.InvariantCulture,
            $"{meanMs:0} ms average with {jitterMs:0.0} ms jitter."));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double rank = percentile / 100d * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }
}
