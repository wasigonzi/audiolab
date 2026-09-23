using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Benchmarking;

/// <summary>
/// The frame time source used when the platform has no implementation.
/// </summary>
/// <remarks>
/// This exists so the Benchmark Lab is constructible everywhere and reports the honest answer —
/// "this platform cannot capture frames" — instead of a comparison built on nothing. It never
/// yields a frame, and the status says why.
/// </remarks>
public sealed class UnavailableFrameTimeSource : IFrameTimeSource
{
    private readonly string _detail;

    /// <summary>Creates the source.</summary>
    /// <param name="detail">
    /// What to tell the user. The default describes a platform with no frame capture at all.
    /// </param>
    public UnavailableFrameTimeSource(string? detail = null) =>
        _detail = detail ?? "This platform has no frame time capture, so no frame measurement can be made.";

    /// <inheritdoc />
    public Task<FrameCaptureStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new FrameCaptureStatus(
            FrameCaptureAvailability.NotImplementedOnThisPlatform, _detail));

    /// <inheritdoc />
    public async IAsyncEnumerable<FramePresentEvent> CaptureAsync(
        int processId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
