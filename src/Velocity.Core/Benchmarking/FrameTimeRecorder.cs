using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Benchmarking;

/// <summary>Frame intervals captured during one measurement.</summary>
/// <param name="FrameTimesMs">Intervals between consecutive presents, in milliseconds.</param>
/// <param name="DiscardedFrames">
/// Presents that were dropped as unusable, with the reason count. A capture that quietly discards
/// frames would make a run look smoother than it was, so the count travels with the result.
/// </param>
public sealed record FrameCapture(
    IReadOnlyList<double> FrameTimesMs,
    IReadOnlyDictionary<string, int> DiscardedFrames)
{
    /// <summary>Number of usable frame intervals.</summary>
    public int Count => FrameTimesMs.Count;
}

/// <summary>
/// Turns a stream of present events into the frame intervals the statistics work on.
/// </summary>
/// <remarks>
/// <para>
/// A present event is a point in time; a frame time is the gap between two of them. The first
/// present therefore produces no interval, which is why a capture of N presents yields N-1 frame
/// times.
/// </para>
/// <para>
/// <b>Two kinds of event are discarded, and both are counted.</b> A non-positive interval means the
/// trace delivered events out of order, and a very long interval means the game was alt-tabbed,
/// loading, or paused at a menu. Keeping either would put a spike in the tail that the user never
/// felt as stutter, and silently dropping them would let a capture flatter itself. So they are
/// dropped and reported.
/// </para>
/// </remarks>
public sealed class FrameTimeRecorder
{
    /// <summary>Reason key for an interval that was not positive.</summary>
    public const string OutOfOrderReason = "out-of-order";

    /// <summary>Reason key for an interval longer than the stall threshold.</summary>
    public const string StallReason = "stall";

    private readonly ILogger<FrameTimeRecorder> _logger;

    /// <summary>Creates the recorder.</summary>
    /// <param name="logger">Logger.</param>
    public FrameTimeRecorder(ILogger<FrameTimeRecorder> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Intervals above this are treated as the game not rendering rather than as a slow frame.
    /// </summary>
    /// <remarks>
    /// One second is two orders of magnitude beyond the worst stutter a player would describe as a
    /// frame. Anything longer is a loading screen or an alt-tab, and counting it as a frame time
    /// would dominate every tail percentile in the run.
    /// </remarks>
    public static double StallThresholdMs => 1000d;

    /// <summary>Converts present timestamps into frame intervals.</summary>
    /// <param name="presentTimestampsMs">Present times in milliseconds, in capture order.</param>
    /// <returns>The capture.</returns>
    public static FrameCapture FromPresents(IReadOnlyList<double> presentTimestampsMs)
    {
        ArgumentNullException.ThrowIfNull(presentTimestampsMs);

        var intervals = new List<double>(Math.Max(0, presentTimestampsMs.Count - 1));
        int outOfOrder = 0;
        int stalls = 0;

        for (int index = 1; index < presentTimestampsMs.Count; index++)
        {
            double interval = presentTimestampsMs[index] - presentTimestampsMs[index - 1];

            if (interval <= 0d || double.IsNaN(interval))
            {
                outOfOrder++;
                continue;
            }

            if (interval > StallThresholdMs)
            {
                stalls++;
                continue;
            }

            intervals.Add(interval);
        }

        var discarded = new Dictionary<string, int>(StringComparer.Ordinal);

        if (outOfOrder > 0)
        {
            discarded[OutOfOrderReason] = outOfOrder;
        }

        if (stalls > 0)
        {
            discarded[StallReason] = stalls;
        }

        return new FrameCapture(intervals, discarded);
    }

    /// <summary>Captures frames for a process until the token is cancelled.</summary>
    /// <param name="source">Frame time source.</param>
    /// <param name="processId">Process to capture, or <c>0</c> for every process.</param>
    /// <param name="cancellationToken">Token that ends the capture.</param>
    /// <returns>The capture.</returns>
    public async Task<FrameCapture> CaptureAsync(
        IFrameTimeSource source,
        int processId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var presents = new List<double>();

        try
        {
            await foreach (FramePresentEvent frame in
                source.CaptureAsync(processId, cancellationToken).ConfigureAwait(false))
            {
                presents.Add(frame.TimestampMs);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is how a capture ends; what was collected up to that point is the result.
        }

        FrameCapture capture = FromPresents(presents);

        if (capture.DiscardedFrames.Count > 0)
        {
            _logger.LogDebug(
                "Captured {Frames} frame intervals; discarded {Discarded}.",
                capture.Count,
                string.Join(", ", capture.DiscardedFrames));
        }

        return capture;
    }
}
