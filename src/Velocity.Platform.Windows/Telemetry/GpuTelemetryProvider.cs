using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Platform.Windows.Telemetry;

/// <summary>
/// Reads graphics utilization and dedicated video memory from the GPU performance counters.
/// </summary>
/// <remarks>
/// <para>
/// Windows publishes GPU counters per process and per engine, with instance names of the form
/// <c>pid_1234_luid_0x…_phys_0_eng_0_engtype_3D</c>. Overall 3D utilization is the sum over the
/// <c>engtype_3D</c> instances; copy, video decode and compute engines are excluded because a game
/// is not made faster or slower by the desktop compositor's copy engine.
/// </para>
/// <para>
/// Instances appear and disappear with every process, so they are re-enumerated on a timer rather
/// than on every sample: enumerating several hundred instances once a second would make the
/// monitor a measurable load in its own right.
/// </para>
/// <para>
/// Utilization can exceed 100% when several engines are busy at once; it is clamped, and the
/// clamping is why this is reported as a utilization indicator rather than as a precise figure.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GpuTelemetryProvider : ITelemetryProvider
{
    private const string EngineCategory = "GPU Engine";
    private const string MemoryCategory = "GPU Process Memory";
    private const string ThreeDimensionalEngineSuffix = "engtype_3D";

    private readonly ILogger<GpuTelemetryProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _instanceRefreshInterval;
    private readonly List<PerformanceCounter> _engineCounters = new();
    private readonly List<PerformanceCounter> _memoryCounters = new();

    private DateTimeOffset _instancesRefreshedAt = DateTimeOffset.MinValue;
    private bool _categoriesAvailable = true;
    private bool _disposed;

    /// <summary>Creates the provider.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injected so the refresh interval is testable.</param>
    /// <param name="instanceRefreshInterval">How often the instance list is rebuilt.</param>
    public GpuTelemetryProvider(
        ILogger<GpuTelemetryProvider> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? instanceRefreshInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instanceRefreshInterval = instanceRefreshInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public string Name => "gpu-counters";

    /// <inheritdoc />
    public Task ContributeAsync(TelemetrySampleBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_categoriesAvailable)
        {
            return Task.CompletedTask;
        }

        RefreshInstancesIfDue();

        double? utilization = SumCounters(_engineCounters);
        builder.GpuUtilization = utilization is null ? null : Math.Clamp(utilization.Value / 100d, 0d, 1d);

        double? memory = SumCounters(_memoryCounters);
        builder.VideoMemoryUsedBytes = memory is null ? null : (long)memory.Value;

        return Task.CompletedTask;
    }

    private void RefreshInstancesIfDue()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now - _instancesRefreshedAt < _instanceRefreshInterval)
        {
            return;
        }

        _instancesRefreshedAt = now;

        try
        {
            RebuildCounters(
                _engineCounters,
                EngineCategory,
                "Utilization Percentage",
                instance => instance.EndsWith(ThreeDimensionalEngineSuffix, StringComparison.OrdinalIgnoreCase));

            RebuildCounters(_memoryCounters, MemoryCategory, "Dedicated Usage", _ => true);
        }
        catch (InvalidOperationException ex)
        {
            // The GPU counter categories are absent on server SKUs and in some virtual machines.
            _logger.LogDebug(ex, "GPU performance counters are not published on this system.");
            _categoriesAvailable = false;
            DisposeCounters();
        }
    }

    private void RebuildCounters(
        List<PerformanceCounter> target,
        string category,
        string counterName,
        Func<string, bool> instanceFilter)
    {
        foreach (PerformanceCounter counter in target)
        {
            counter.Dispose();
        }

        target.Clear();

        var performanceCategory = new PerformanceCounterCategory(category);

        foreach (string instance in performanceCategory.GetInstanceNames().Where(instanceFilter))
        {
            try
            {
                target.Add(new PerformanceCounter(category, counterName, instance, readOnly: true));
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                // The owning process exited between enumeration and counter creation; skip it.
                _logger.LogTrace(ex, "GPU counter instance {Instance} vanished.", instance);
            }
        }
    }

    private double? SumCounters(List<PerformanceCounter> counters)
    {
        if (counters.Count == 0)
        {
            return null;
        }

        double total = 0d;
        bool any = false;

        for (int i = counters.Count - 1; i >= 0; i--)
        {
            try
            {
                total += counters[i].NextValue();
                any = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                // The instance disappeared with its process; drop it until the next refresh.
                _logger.LogTrace(ex, "Dropping a GPU counter whose instance disappeared.");
                counters[i].Dispose();
                counters.RemoveAt(i);
            }
        }

        return any ? total : null;
    }

    private void DisposeCounters()
    {
        foreach (PerformanceCounter counter in _engineCounters.Concat(_memoryCounters))
        {
            counter.Dispose();
        }

        _engineCounters.Clear();
        _memoryCounters.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCounters();
    }
}
