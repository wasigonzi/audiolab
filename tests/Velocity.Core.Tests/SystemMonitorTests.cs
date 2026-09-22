using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Telemetry;
using Velocity.Core.Telemetry;

namespace Velocity.Core.Tests;

/// <summary>
/// The monitor runs for the whole life of the application, including while a game is running, so
/// its failure behaviour matters more than its happy path.
/// </summary>
public sealed class SystemMonitorTests
{
    [Fact]
    public async Task Sample_CombinesEveryProvidersContribution()
    {
        await using var monitor = new SystemMonitor(
            new ITelemetryProvider[]
            {
                new DelegateProvider("cpu", builder => builder.CpuUtilization = 0.42d),
                new DelegateProvider("memory", builder =>
                {
                    builder.MemoryUsedBytes = 8L * 1024 * 1024 * 1024;
                    builder.MemoryTotalBytes = 32L * 1024 * 1024 * 1024;
                }),
            },
            NullLogger<SystemMonitor>.Instance);

        TelemetrySample sample = await monitor.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(0.42d, sample.CpuUtilization);
        Assert.Equal(0.25d, sample.MemoryUtilization);
    }

    [Fact]
    public async Task AnUnavailableCounter_ReadsAsNullRatherThanZero()
    {
        await using var monitor = new SystemMonitor(
            Array.Empty<ITelemetryProvider>(), NullLogger<SystemMonitor>.Instance);

        TelemetrySample sample = await monitor.SampleOnceAsync(CancellationToken.None);

        // A dashboard showing 0% GPU because the counter is missing would be a lie.
        Assert.Null(sample.GpuUtilization);
        Assert.Null(sample.CpuUtilization);
        Assert.Null(sample.MemoryUtilization);
    }

    [Fact]
    public async Task AFailingProvider_DoesNotStopTheOthers()
    {
        var working = new DelegateProvider("cpu", builder => builder.CpuUtilization = 0.5d);
        var failing = new DelegateProvider("gpu", _ => throw new InvalidOperationException("counter gone"));

        await using var monitor = new SystemMonitor(
            new ITelemetryProvider[] { failing, working }, NullLogger<SystemMonitor>.Instance);

        TelemetrySample sample = await monitor.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(0.5d, sample.CpuUtilization);
        Assert.Null(sample.GpuUtilization);
    }

    [Fact]
    public async Task AProviderThatKeepsFailing_IsDroppedRatherThanRetriedForever()
    {
        var failing = new DelegateProvider("gpu", _ => throw new InvalidOperationException("counter gone"));

        await using var monitor = new SystemMonitor(
            new ITelemetryProvider[] { failing },
            NullLogger<SystemMonitor>.Instance,
            new SystemMonitorOptions { ProviderFailureLimit = 3 });

        for (int i = 0; i < 5; i++)
        {
            await monitor.SampleOnceAsync(CancellationToken.None);
        }

        // Retrying a counter that is broken on this machine every second is exactly the background
        // work this product exists to remove.
        Assert.Equal(3, failing.CallCount);
    }

    [Fact]
    public async Task ATransientFailure_DoesNotCountTowardsTheDropLimit()
    {
        int calls = 0;
        var flaky = new DelegateProvider("disk", builder =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("transient");
            }

            builder.DiskBytesPerSecond = 1024d;
        });

        await using var monitor = new SystemMonitor(
            new ITelemetryProvider[] { flaky },
            NullLogger<SystemMonitor>.Instance,
            new SystemMonitorOptions { ProviderFailureLimit = 2 });

        await monitor.SampleOnceAsync(CancellationToken.None);
        await monitor.SampleOnceAsync(CancellationToken.None);
        TelemetrySample third = await monitor.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(1024d, third.DiskBytesPerSecond);
    }

    [Fact]
    public async Task Start_PublishesSamplesUntilStopped()
    {
        var received = new TaskCompletionSource<TelemetrySample>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var monitor = new SystemMonitor(
            new ITelemetryProvider[] { new DelegateProvider("cpu", builder => builder.CpuUtilization = 0.1d) },
            NullLogger<SystemMonitor>.Instance,
            new SystemMonitorOptions { ForegroundInterval = TimeSpan.FromMilliseconds(20) });

        monitor.SampleProduced += (_, sample) => received.TrySetResult(sample);

        await monitor.StartAsync(MonitorCadence.Foreground, CancellationToken.None);

        TelemetrySample sample = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0.1d, sample.CpuUtilization);
        Assert.Equal(MonitorCadence.Foreground, monitor.Cadence);

        await monitor.StopAsync();
        Assert.Equal(MonitorCadence.Stopped, monitor.Cadence);
    }

    [Fact]
    public async Task Cadence_CanBeChangedWhileRunning()
    {
        await using var monitor = new SystemMonitor(
            Array.Empty<ITelemetryProvider>(),
            NullLogger<SystemMonitor>.Instance,
            new SystemMonitorOptions { ForegroundInterval = TimeSpan.FromMilliseconds(50) });

        await monitor.StartAsync(MonitorCadence.Foreground, CancellationToken.None);
        monitor.SetCadence(MonitorCadence.GamingSession);

        // Sampling slows down for the duration of a game rather than the loop being torn down.
        Assert.Equal(MonitorCadence.GamingSession, monitor.Cadence);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task Stop_IsSafeWhenTheMonitorWasNeverStarted()
    {
        await using var monitor = new SystemMonitor(
            Array.Empty<ITelemetryProvider>(), NullLogger<SystemMonitor>.Instance);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task Dispose_DisposesEveryProvider()
    {
        var provider = new DelegateProvider("cpu", _ => { });
        var monitor = new SystemMonitor(
            new ITelemetryProvider[] { provider }, NullLogger<SystemMonitor>.Instance);

        await monitor.DisposeAsync();

        Assert.True(provider.Disposed);
    }

    private sealed class DelegateProvider : ITelemetryProvider
    {
        private readonly Action<TelemetrySampleBuilder> _contribute;

        internal DelegateProvider(string name, Action<TelemetrySampleBuilder> contribute)
        {
            Name = name;
            _contribute = contribute;
        }

        public string Name { get; }

        internal int CallCount { get; private set; }

        internal bool Disposed { get; private set; }

        public Task ContributeAsync(TelemetrySampleBuilder builder, CancellationToken cancellationToken)
        {
            CallCount++;
            _contribute(builder);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}
