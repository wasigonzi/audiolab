using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Platform.Windows.Telemetry;

/// <summary>
/// Reads processor, memory, disk and network counters through the performance counter API.
/// </summary>
/// <remarks>
/// <para>
/// Counters are created once and reused. Constructing a <see cref="PerformanceCounter"/> is
/// expensive — it opens the PDH query and resolves the instance — and doing it per sample would
/// make the monitor itself a measurable load, which is precisely what this product must not be.
/// </para>
/// <para>
/// Rate counters need two reads before they mean anything, so the first sample after construction
/// is primed rather than reported. Counters that do not exist on a given edition are skipped, and
/// the corresponding field stays null.
/// </para>
/// <para>
/// <c>% Processor Utility</c> is preferred over <c>% Processor Time</c>: the latter reports against
/// the nominal clock and reads above 100% on a boosting processor, which makes a gauge meaningless.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PerformanceCounterTelemetryProvider : ITelemetryProvider
{
    private readonly ILogger<PerformanceCounterTelemetryProvider> _logger;
    private readonly ISystemProfileProvider _profileProvider;
    private readonly List<PerformanceCounter> _owned = new();
    private readonly List<PerformanceCounter> _perCoreCounters = new();

    private int _baseFrequencyMhz;
    private bool _countersOpened;

    private PerformanceCounter? _cpuTotal;
    private PerformanceCounter? _cpuPerformance;
    private PerformanceCounter? _availableBytes;
    private PerformanceCounter? _committedBytes;
    private PerformanceCounter? _diskBytesPerSecond;
    private PerformanceCounter? _diskQueueLength;
    private PerformanceCounter? _networkBytesPerSecond;
    private long _totalPhysicalBytes;
    private bool _primed;
    private bool _disposed;

    /// <summary>Creates the provider.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="profileProvider">
    /// Source of installed memory and the nominal clock. It is read on the first sample rather than
    /// in the constructor: capturing the machine profile is an async operation, and blocking on it
    /// inside a dependency injection factory is a deadlock waiting to happen.
    /// </param>
    public PerformanceCounterTelemetryProvider(
        ILogger<PerformanceCounterTelemetryProvider> logger,
        ISystemProfileProvider profileProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
    }

    private void OpenCounters()
    {
        _cpuTotal = TryCreate("Processor Information", "% Processor Utility", "_Total")
                    ?? TryCreate("Processor", "% Processor Time", "_Total");
        _cpuPerformance = TryCreate("Processor Information", "% Processor Performance", "_Total");
        _availableBytes = TryCreate("Memory", "Available Bytes");
        _committedBytes = TryCreate("Memory", "Committed Bytes");
        _diskBytesPerSecond = TryCreate("PhysicalDisk", "Disk Bytes/sec", "_Total");
        _diskQueueLength = TryCreate("PhysicalDisk", "Current Disk Queue Length", "_Total");
        _networkBytesPerSecond = TryCreate("Network Interface", "Bytes Total/sec", instanceName: null, aggregate: true);

        CreatePerCoreCounters();
    }

    private async Task EnsureOpenedAsync(CancellationToken cancellationToken)
    {
        if (_countersOpened)
        {
            return;
        }

        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        _totalPhysicalBytes = profile.Memory.TotalPhysicalBytes;
        _baseFrequencyMhz = profile.Cpu.BaseFrequencyMhz;

        OpenCounters();
        _countersOpened = true;
    }

    /// <inheritdoc />
    public string Name => "performance-counters";

    /// <inheritdoc />
    public async Task ContributeAsync(TelemetrySampleBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureOpenedAsync(cancellationToken).ConfigureAwait(false);

        if (!_primed)
        {
            // Rate counters report zero on their first read; prime them and report nothing.
            PrimeAll();
            _primed = true;
            return;
        }

        builder.CpuUtilization = ReadFraction(_cpuTotal);

        if (_cpuPerformance is not null && _baseFrequencyMhz > 0)
        {
            double? performance = ReadRaw(_cpuPerformance);
            builder.CpuFrequencyMhz = performance is null ? null : _baseFrequencyMhz * performance.Value / 100d;
        }

        if (_perCoreCounters.Count > 0)
        {
            var perCore = new List<double>(_perCoreCounters.Count);
            foreach (PerformanceCounter counter in _perCoreCounters)
            {
                perCore.Add(ReadFraction(counter) ?? 0d);
            }

            builder.PerCoreUtilization = perCore;
        }

        double? available = ReadRaw(_availableBytes);
        if (available is not null && _totalPhysicalBytes > 0)
        {
            builder.MemoryTotalBytes = _totalPhysicalBytes;
            builder.MemoryUsedBytes = Math.Max(0L, _totalPhysicalBytes - (long)available.Value);
        }

        builder.CommittedBytes = ReadRaw(_committedBytes) is double committed ? (long)committed : null;
        builder.DiskBytesPerSecond = ReadRaw(_diskBytesPerSecond);
        builder.DiskQueueLength = ReadRaw(_diskQueueLength);
        builder.NetworkBytesPerSecond = ReadNetworkTotal();
    }

    private void CreatePerCoreCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("Processor Information");

            // Instance names are "group,index" on multi-group machines and plain indexes otherwise.
            // Sorting numerically keeps the core strip in processor order rather than string order.
            IEnumerable<string> instances = category.GetInstanceNames()
                .Where(name => !name.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(ParseInstanceOrder);

            foreach (string instance in instances)
            {
                PerformanceCounter? counter =
                    TryCreate("Processor Information", "% Processor Utility", instance)
                    ?? TryCreate("Processor Information", "% Processor Time", instance);

                if (counter is not null)
                {
                    _perCoreCounters.Add(counter);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Per core processor counters are unavailable.");
        }
    }

    private static (int Group, int Index) ParseInstanceOrder(string instanceName)
    {
        string[] parts = instanceName.Split(',');

        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int group) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? (group, index)
            : int.TryParse(instanceName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flat)
                ? (0, flat)
                : (int.MaxValue, int.MaxValue);
    }

    private readonly List<PerformanceCounter> _networkCounters = new();

    private PerformanceCounter? TryCreate(
        string category,
        string counter,
        string? instanceName = null,
        bool aggregate = false)
    {
        try
        {
            if (aggregate)
            {
                var networkCategory = new PerformanceCounterCategory(category);
                foreach (string instance in networkCategory.GetInstanceNames())
                {
                    var adapterCounter = new PerformanceCounter(category, counter, instance, readOnly: true);
                    _networkCounters.Add(adapterCounter);
                    _owned.Add(adapterCounter);
                }

                return _networkCounters.Count > 0 ? _networkCounters[0] : null;
            }

            var created = instanceName is null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instanceName, readOnly: true);

            _owned.Add(created);
            return created;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            // A counter that this edition does not publish must leave its field null, not zero.
            _logger.LogDebug(ex, "Counter {Category}\\{Counter} is unavailable.", category, counter);
            return null;
        }
    }

    private void PrimeAll()
    {
        foreach (PerformanceCounter counter in _owned)
        {
            _ = ReadRaw(counter);
        }
    }

    private double? ReadNetworkTotal()
    {
        if (_networkCounters.Count == 0)
        {
            return null;
        }

        double total = 0d;
        bool any = false;

        foreach (PerformanceCounter counter in _networkCounters)
        {
            double? value = ReadRaw(counter);
            if (value is not null)
            {
                total += value.Value;
                any = true;
            }
        }

        return any ? total : null;
    }

    private double? ReadFraction(PerformanceCounter? counter)
    {
        double? value = ReadRaw(counter);
        return value is null ? null : Math.Clamp(value.Value / 100d, 0d, 1d);
    }

    private double? ReadRaw(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return null;
        }

        try
        {
            return counter.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Counter {Counter} could not be read.", counter.CounterName);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (PerformanceCounter counter in _owned)
        {
            counter.Dispose();
        }

        _owned.Clear();
        _perCoreCounters.Clear();
        _networkCounters.Clear();
        _cpuTotal = null;
        _cpuPerformance = null;
        _availableBytes = null;
        _committedBytes = null;
        _diskBytesPerSecond = null;
        _diskQueueLength = null;
        _networkBytesPerSecond = null;
        _totalPhysicalBytes = 0;
    }
}
