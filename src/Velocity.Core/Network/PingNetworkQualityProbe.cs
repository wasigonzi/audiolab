using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Network;

namespace Velocity.Core.Network;

/// <summary>Measures connection quality with ICMP echo.</summary>
/// <remarks>
/// <para>
/// Probes are sent one at a time with a short gap, not in a burst. A burst measures how the path
/// handles a burst, which is not what a game does; spacing them measures the steady state the game
/// actually experiences.
/// </para>
/// <para>
/// ICMP is de-prioritised or dropped by some networks, so a poor result is reported as "this is
/// what ICMP saw" rather than as a verdict on the game's traffic. It is still the right tool for
/// detecting jitter and loss on the local link, which is the part a setting on this machine could
/// plausibly change.
/// </para>
/// </remarks>
public sealed class PingNetworkQualityProbe : INetworkQualityProbe
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(200);
    private const int ProbeTimeoutMs = 1000;

    private readonly ILogger<PingNetworkQualityProbe> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injected so tests can control timestamps.</param>
    public PingNetworkQualityProbe(
        ILogger<PingNetworkQualityProbe> logger,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<NetworkQualityMeasurement> MeasureAsync(
        string endpoint,
        int probeCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(probeCount);

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        var roundTrips = new List<double>(probeCount);
        int sent = 0;

        using var ping = new Ping();

        for (int i = 0; i < probeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                PingReply reply = await ping.SendPingAsync(endpoint, ProbeTimeoutMs).ConfigureAwait(false);
                sent++;

                if (reply.Status == IPStatus.Success)
                {
                    roundTrips.Add(reply.RoundtripTime);
                }
            }
            catch (PingException ex)
            {
                sent++;
                _logger.LogDebug(ex, "A probe to {Endpoint} failed.", endpoint);
            }

            if (i < probeCount - 1)
            {
                await Task.Delay(ProbeInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return NetworkQualityAnalyzer.Analyze(endpoint, startedAt, sent, roundTrips);
    }
}
