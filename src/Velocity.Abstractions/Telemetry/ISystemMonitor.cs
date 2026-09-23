using System;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Telemetry;

/// <summary>How often the monitor samples, which depends on what the user is doing.</summary>
/// <remarks>
/// The optimizer must not become a source of stutter. When a game is running, or the window is
/// hidden, sampling drops to a rate that still keeps a session record without competing with the
/// game for CPU and disk.
/// </remarks>
public enum MonitorCadence
{
    /// <summary>Not sampling.</summary>
    Stopped = 0,

    /// <summary>The dashboard is visible and in the foreground.</summary>
    Foreground = 1,

    /// <summary>The window is minimised or hidden.</summary>
    Background = 2,

    /// <summary>A game is running; sampling is at its slowest.</summary>
    GamingSession = 3,
}

/// <summary>Polls the telemetry providers and publishes samples.</summary>
public interface ISystemMonitor : IAsyncDisposable
{
    /// <summary>The most recent sample, or <see langword="null"/> before the first one.</summary>
    TelemetrySample? Latest { get; }

    /// <summary>The cadence currently in effect.</summary>
    MonitorCadence Cadence { get; }

    /// <summary>Raised on the sampling thread each time a sample is produced.</summary>
    event EventHandler<TelemetrySample>? SampleProduced;

    /// <summary>Starts sampling.</summary>
    /// <param name="cadence">Initial cadence.</param>
    /// <param name="cancellationToken">Token used to abort the start.</param>
    /// <returns>A task that completes once sampling has started.</returns>
    Task StartAsync(MonitorCadence cadence, CancellationToken cancellationToken);

    /// <summary>Changes the sampling cadence without restarting.</summary>
    /// <param name="cadence">New cadence.</param>
    void SetCadence(MonitorCadence cadence);

    /// <summary>Stops sampling.</summary>
    /// <returns>A task that completes once sampling has stopped.</returns>
    Task StopAsync();
}
