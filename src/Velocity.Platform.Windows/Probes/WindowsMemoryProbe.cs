using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Reads memory capacity, availability and commit state.</summary>
/// <remarks>
/// Commit charge and the commit limit are read as well as the headline "available memory", because
/// a machine that is comfortable on available memory but close to its commit limit will still
/// stutter, and the headline number alone would hide that.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMemoryProbe : IMemoryProbe
{
    private readonly ILogger<WindowsMemoryProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsMemoryProbe(ILogger<WindowsMemoryProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "memory";

    /// <inheritdoc />
    public Task<MemoryInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(), "Could not read memory status.");
        }

        var performance = new NativeMethods.PerformanceInformation
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.PerformanceInformation>(),
        };

        bool havePerformance = NativeMethods.GetPerformanceInfo(ref performance, performance.Size);
        long pageSize = havePerformance ? (long)performance.PageSize : 4096;

        return Task.FromResult(new MemoryInfo
        {
            TotalPhysicalBytes = (long)status.TotalPhysical,
            AvailablePhysicalBytes = (long)status.AvailablePhysical,
            CommitTotalBytes = havePerformance ? (long)performance.CommitTotal * pageSize : 0,
            CommitLimitBytes = havePerformance ? (long)performance.CommitLimit * pageSize : 0,
            CommitPeakBytes = havePerformance ? (long)performance.CommitPeak * pageSize : 0,
            PageSizeBytes = (int)pageSize,
            Modules = ReadModules(),
            PageFileSizeBytes = EstimatePageFileBytes(status),
        });
    }

    /// <summary>
    /// Derives the page file size from the commit limit reported by <c>GlobalMemoryStatusEx</c>.
    /// </summary>
    /// <remarks>
    /// <c>TotalPageFile</c> is the system commit limit, which is physical memory plus the page
    /// files. The difference is therefore the page file contribution; a non-positive difference
    /// means the value is not usable and null is returned rather than a misleading zero.
    /// </remarks>
    private static long? EstimatePageFileBytes(NativeMethods.MemoryStatusEx status)
    {
        long difference = (long)status.TotalPageFile - (long)status.TotalPhysical;
        return difference > 0 ? difference : null;
    }

    private IReadOnlyList<MemoryModule> ReadModules()
    {
        var modules = new List<MemoryModule>();

        try
        {
            foreach (WmiRecord record in WmiQuery.Run(
                "SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
            {
                modules.Add(new MemoryModule
                {
                    Slot = record.GetString("DeviceLocator") ?? "Unknown",
                    CapacityBytes = (long)(record.GetUInt64("Capacity") ?? 0UL),
                    ConfiguredSpeedMtps = (int)(record.GetUInt32("ConfiguredClockSpeed") ?? 0U),
                    Manufacturer = record.GetString("Manufacturer")?.Trim(),
                    PartNumber = record.GetString("PartNumber")?.Trim(),
                });
            }
        }
        catch (Exception ex)
        {
            // SMBIOS data is unavailable on some virtual machines and locked down images. The rest
            // of the memory information is still valid, so the module list is simply left empty.
            _logger.LogDebug(ex, "Physical memory module information is unavailable.");
        }

        return modules;
    }
}
