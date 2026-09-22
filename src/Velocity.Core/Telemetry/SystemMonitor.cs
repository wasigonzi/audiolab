using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.Telemetry;

/// <summary>Sampling intervals for each cadence.</summary>
public sealed class SystemMonitorOptions
{
    /// <summary>Interval while the dashboard is in the foreground.</summary>
    public TimeSpan ForegroundInterval { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Interval while the window is minimised.</summary>
    public TimeSpan BackgroundInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Interval while a game is running. Deliberately slow: the optimizer must not compete with
    /// the game it is supposed to be protecting.
    /// </summary>
    public TimeSpan GamingSessionInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many consecutive failures a provider may produce before it is dropped for the rest of
    /// the session.
    /// </summary>
    public int ProviderFailureLimit { get; set; } = 3;
}

/// <summary>
/// Default <see cref="ISystemMonitor"/>: a single timer loop over the registered providers.
/// </summary>
/// <remarks>
/// <para>
/// One loop, one timer, no thread per counter. A provider that throws repeatedly is dropped rather
/// than retried forever, because a counter that is broken on this machine will stay broken and
/// retrying it every second is exactly the kind of background work this product exists to remove.
/// </para>
/// <para>
/// Sampling is driven by <see cref="PeriodicTimer"/> rather than a sleep loop so that changing
/// cadence takes effect on the next tick without tearing down the loop.
/// </para>
/// </remarks>
public sealed class SystemMonitor : ISystemMonitor
{
    private readonly IReadOnlyList<ITelemetryProvider> _providers;
    private readonly SystemMonitorOptions _options;
    private readonly ILogger<SystemMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, int> _failureCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _droppedProviders = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private int _cadence = (int)MonitorCadence.Stopped;

    /// <summary>Creates the monitor.</summary>
    /// <param name="providers">Telemetry providers registered for this platform.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Sampling intervals.</param>
    /// <param name="timeProvider">Clock, injected so tests can drive the loop deterministically.</param>
    public SystemMonitor(
        IEnumerable<ITelemetryProvider> providers,
        ILogger<SystemMonitor> logger,
        SystemMonitorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = new List<ITelemetryProvider>(providers);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SystemMonitorOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public TelemetrySample? Latest { get; private set; }

    /// <inheritdoc />
    public MonitorCadence Cadence => (MonitorCadence)Volatile.Read(ref _cadence);

    /// <inheritdoc />
    public event EventHandler<TelemetrySample>? SampleProduced;

    /// <inheritdoc />
    public async Task StartAsync(MonitorCadence cadence, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                SetCadence(cadence);
                return;
            }

            Volatile.Write(ref _cadence, (int)cadence);
            _loopCancellation = new CancellationTokenSource();
            _loop = RunAsync(_loopCancellation.Token);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc />
    public void SetCadence(MonitorCadence cadence) => Volatile.Write(ref _cadence, (int)cadence);

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loopCancellation is null)
            {
                return;
            }

            await _loopCancellation.CancelAsync().ConfigureAwait(false);

            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on stop.
                }
            }

            _loopCancellation.Dispose();
            _loopCancellation = null;
            _loop = null;
            Volatile.Write(ref _cadence, (int)MonitorCadence.Stopped);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Takes exactly one sample. Exposed so a caller that wants a reading without running the loop
    /// (a benchmark run, a test) does not have to start and stop the monitor.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the sample.</param>
    /// <returns>The sample.</returns>
    public async Task<TelemetrySample> SampleOnceAsync(CancellationToken cancellationToken)
    {
        var builder = new TelemetrySampleBuilder();

        foreach (ITelemetryProvider provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_droppedProviders.Contains(provider.Name))
            {
                continue;
            }

            try
            {
                await provider.ContributeAsync(builder, cancellationToken).ConfigureAwait(false);
                _failureCounts.Remove(provider.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordProviderFailure(provider, ex);
            }
        }

        TelemetrySample sample = builder.Build(_timeProvider.GetUtcNow());
        Latest = sample;
        return sample;
    }

    private void RecordProviderFailure(ITelemetryProvider provider, Exception exception)
    {
        int failures = _failureCounts.TryGetValue(provider.Name, out int existing) ? existing + 1 : 1;
        _failureCounts[provider.Name] = failures;

        if (failures >= _options.ProviderFailureLimit)
        {
            _droppedProviders.Add(provider.Name);
            _logger.LogWarning(
                exception,
                "Telemetry provider {Provider} failed {Count} times and will not be sampled again this session.",
                provider.Name,
                failures);
        }
        else
        {
            _logger.LogDebug(exception, "Telemetry provider {Provider} failed.", provider.Name);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = IntervalFor(Cadence);
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TimeSpan wanted = IntervalFor(Cadence);
            if (wanted != interval)
            {
                interval = wanted;
                timer.Period = interval;
            }

            try
            {
                TelemetrySample sample = await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
                SampleProduced?.Invoke(this, sample);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad tick must not end the loop; the dashboard simply keeps its last sample.
                _logger.LogWarning(ex, "A telemetry sample failed.");
            }
        }
    }

    private TimeSpan IntervalFor(MonitorCadence cadence) => cadence switch
    {
        MonitorCadence.Foreground => _options.ForegroundInterval,
        MonitorCadence.Background => _options.BackgroundInterval,
        MonitorCadence.GamingSession => _options.GamingSessionInterval,
        _ => _options.BackgroundInterval,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        foreach (ITelemetryProvider provider in _providers)
        {
            provider.Dispose();
        }

        _stateLock.Dispose();
    }
}
