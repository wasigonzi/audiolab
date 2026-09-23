using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Platform.Windows.Telemetry;

/// <summary>
/// Captures frame presents from the kernel graphics ETW provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this reads.</b> A real-time ETW session subscribed to
/// <c>Microsoft-Windows-DxgKrnl</c>, keyword <c>Presents</c>. Its present events are the same
/// source PresentMon uses, and they are the only documented way to observe another process's frame
/// times on Windows. There is no public API for it, so there is no lighter alternative to fall back
/// to.
/// </para>
/// <para>
/// <b>Why it can refuse to start.</b> Creating a real-time ETW session requires administrative
/// rights, and a session name can only be held by one process at a time. Both are reported through
/// <see cref="GetStatusAsync"/> rather than surfacing as an empty capture, because a benchmark with
/// no frames behind it must never be presented as a measurement.
/// </para>
/// <para>
/// <b>What is measured.</b> The timestamp of each present, which is when the frame reached the
/// compositor. That is the right measurement for pacing and stutter and it is what frame time tools
/// on Windows report, but it is not the moment the GPU finished rendering, and the product does not
/// claim otherwise.
/// </para>
/// <para>
/// The trace callback runs on the ETW processing thread, so it does the least work possible: it
/// writes one timestamp into a bounded channel and returns. Dropping the oldest entry under
/// pressure is preferred to blocking, because blocking that thread loses events for the whole
/// machine, not just for this capture.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EtwFrameTimeSource : IFrameTimeSource
{
    /// <summary>Provider exposing present events.</summary>
    public const string ProviderName = "Microsoft-Windows-DxgKrnl";

    /// <summary>The <c>Presents</c> keyword on that provider.</summary>
    public const ulong PresentsKeyword = 0x1;

    private const string SessionName = "VelocityFrameCapture";
    private const int ChannelCapacity = 1 << 16;

    private readonly ILogger<EtwFrameTimeSource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="logger">Logger.</param>
    public EtwFrameTimeSource(ILogger<EtwFrameTimeSource> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<FrameCaptureStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsElevated())
        {
            return Task.FromResult(new FrameCaptureStatus(
                FrameCaptureAvailability.RequiresElevation,
                "Frame time capture reads the kernel graphics trace provider, which Windows only " +
                "opens to an administrator. Run this measurement from an elevated session; nothing " +
                "else in this product needs it."));
        }

        if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
        {
            return Task.FromResult(new FrameCaptureStatus(
                FrameCaptureAvailability.SessionAlreadyInUse,
                "A previous frame capture session is still running. Close the other instance, or " +
                "run 'logman stop " + SessionName + " -ets' to release it."));
        }

        return Task.FromResult(new FrameCaptureStatus(
            FrameCaptureAvailability.Available, "Frame capture is available."));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FramePresentEvent> CaptureAsync(
        int processId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<FramePresentEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            // Losing the oldest frame beats blocking the ETW thread, which would drop events for
            // every trace consumer on the machine.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        using var session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
        };

        var clock = Stopwatch.StartNew();

        session.Source.Dynamic.All += traceEvent =>
        {
            if (processId != 0 && traceEvent.ProcessID != processId)
            {
                return;
            }

            if (!traceEvent.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            channel.Writer.TryWrite(new FramePresentEvent(traceEvent.ProcessID, clock.Elapsed.TotalMilliseconds));
        };

        session.EnableProvider(ProviderName, TraceEventLevel.Informational, PresentsKeyword);

        // TraceEventSource.Process blocks, so it runs off the caller's thread and is stopped by
        // disposing the session when the token fires.
        Task processing = Task.Run(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The frame capture trace session ended unexpectedly.");
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        },
        CancellationToken.None);

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                session.Stop();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The session is already gone; the channel completing is what ends the capture.
            }

            channel.Writer.TryComplete();
        });

        try
        {
            await foreach (FramePresentEvent frame in
                channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            await processing.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
