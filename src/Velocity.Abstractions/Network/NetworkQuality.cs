using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Network;

/// <summary>How good a connection looks for a real time game.</summary>
public enum ConnectionQuality
{
    /// <summary>Not enough data to say.</summary>
    Unknown = 0,

    /// <summary>Low latency, low jitter, no loss.</summary>
    Excellent = 1,

    /// <summary>Usable for competitive play.</summary>
    Good = 2,

    /// <summary>Playable, but jitter or latency will be noticeable.</summary>
    Fair = 3,

    /// <summary>Loss or jitter high enough to affect play regardless of any setting.</summary>
    Poor = 4,
}

/// <summary>
/// A measured picture of a connection.
/// </summary>
/// <remarks>
/// This exists so the network module can make claims it can support. A registry value that
/// allegedly "reduces ping" is a claim about this measurement, and the product's position is that
/// if it cannot be seen here, it did not happen.
/// </remarks>
public sealed record NetworkQualityMeasurement
{
    /// <summary>Endpoint the measurement was taken against.</summary>
    public required string Endpoint { get; init; }

    /// <summary>When the measurement started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Number of probes sent.</summary>
    public required int ProbesSent { get; init; }

    /// <summary>Number of replies received.</summary>
    public required int RepliesReceived { get; init; }

    /// <summary>Mean round trip time in milliseconds.</summary>
    public double MeanRoundTripMs { get; init; }

    /// <summary>Median round trip time in milliseconds.</summary>
    public double MedianRoundTripMs { get; init; }

    /// <summary>Lowest round trip time observed.</summary>
    public double MinimumRoundTripMs { get; init; }

    /// <summary>95th percentile round trip time.</summary>
    public double P95RoundTripMs { get; init; }

    /// <summary>
    /// Mean absolute difference between consecutive round trip times, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Jitter is measured the way it is felt: consecutive variation, not standard deviation around
    /// a mean. A connection that alternates 20 ms and 60 ms feels far worse than one that drifts
    /// slowly between the same values, and only the consecutive measure shows that.
    /// </remarks>
    public double JitterMs { get; init; }

    /// <summary>Fraction of probes that received no reply, from 0 to 1.</summary>
    public double PacketLoss => ProbesSent == 0 ? 0d : 1d - ((double)RepliesReceived / ProbesSent);

    /// <summary>Overall assessment.</summary>
    public ConnectionQuality Quality { get; init; } = ConnectionQuality.Unknown;

    /// <summary>Plain language explanation of the assessment.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>Measures connection quality.</summary>
public interface INetworkQualityProbe
{
    /// <summary>Measures a connection.</summary>
    /// <param name="endpoint">Host name or address to probe.</param>
    /// <param name="probeCount">Number of probes to send.</param>
    /// <param name="cancellationToken">Token used to abort the measurement.</param>
    /// <returns>The measurement.</returns>
    Task<NetworkQualityMeasurement> MeasureAsync(
        string endpoint,
        int probeCount,
        CancellationToken cancellationToken);
}
