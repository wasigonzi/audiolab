using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Telemetry;

/// <summary>One presented frame.</summary>
/// <param name="ProcessId">Process that presented the frame.</param>
/// <param name="TimestampMs">
/// Time of the present, in milliseconds since the capture started. A capture relative clock is used
/// rather than wall time because frame intervals are what matters and a wall clock adjustment
/// mid-capture would show up as a fabricated stutter.
/// </param>
public readonly record struct FramePresentEvent(int ProcessId, double TimestampMs);

/// <summary>Why frame capture is unavailable.</summary>
public enum FrameCaptureAvailability
{
    /// <summary>Capture can be started.</summary>
    Available = 0,

    /// <summary>The platform has no frame capture implementation at all.</summary>
    NotImplementedOnThisPlatform = 1,

    /// <summary>
    /// The provider needs an elevated kernel trace session, and this process cannot obtain one.
    /// </summary>
    RequiresElevation = 2,

    /// <summary>Another process already holds the trace session this needs.</summary>
    SessionAlreadyInUse = 3,
}

/// <summary>Whether frame capture can run, and why not when it cannot.</summary>
/// <param name="Availability">The state.</param>
/// <param name="Detail">What to tell the user, in plain language.</param>
public readonly record struct FrameCaptureStatus(FrameCaptureAvailability Availability, string Detail)
{
    /// <summary>Whether capture can be started.</summary>
    public bool CanCapture => Availability == FrameCaptureAvailability.Available;
}

/// <summary>
/// Captures frame present times for a process.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this needs, and why it is not always available.</b> Windows exposes no public API that
/// reports another process's frame times. The only documented mechanism is the kernel graphics
/// trace provider, <c>Microsoft-Windows-DxgKrnl</c>, whose present events are what PresentMon
/// reads. Subscribing to it requires a real-time ETW session, which requires administrative
/// rights.
/// </para>
/// <para>
/// That is a real constraint, not an implementation gap, so it is modelled rather than hidden:
/// <see cref="GetStatusAsync"/> reports why capture cannot start, and the Benchmark Lab shows the
/// reason instead of quietly producing a comparison with no frame data behind it.
/// </para>
/// <para>
/// <b>Frame times are presents, not renders.</b> These events record when a frame reached the
/// compositor. That is the right measurement for stutter and pacing and it is what every frame time
/// tool on Windows reports, but it is not the same as the time the GPU finished the frame, and this
/// product does not present it as such.
/// </para>
/// </remarks>
public interface IFrameTimeSource : IAsyncDisposable
{
    /// <summary>Reports whether capture can run on this machine right now.</summary>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns>The status.</returns>
    Task<FrameCaptureStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Captures frame presents for a process until the token is cancelled.
    /// </summary>
    /// <param name="processId">Process to capture, or <c>0</c> for every process.</param>
    /// <param name="cancellationToken">Token that ends the capture.</param>
    /// <returns>The frames, streamed as they are observed.</returns>
    IAsyncEnumerable<FramePresentEvent> CaptureAsync(int processId, CancellationToken cancellationToken);
}
