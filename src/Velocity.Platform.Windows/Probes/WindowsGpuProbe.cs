using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Enumerates display adapters and reads the settings Windows exposes for them.</summary>
/// <remarks>
/// <para>
/// Adapter identity comes from WMI; dedicated video memory comes from the display class registry
/// key, because <c>Win32_VideoController.AdapterRAM</c> is a 32 bit field and silently wraps on any
/// adapter with 4 GB or more.
/// </para>
/// <para>
/// Only settings Windows itself owns are reported. Vendor control panel state (NVIDIA low latency
/// mode, AMD Anti-Lag) is not readable through any documented Windows interface, and this product
/// does not claim to read or change it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsGpuProbe : IGpuProbe
{
    private const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private readonly ILogger<WindowsGpuProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsGpuProbe(ILogger<WindowsGpuProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "gpu";

    /// <inheritdoc />
    public Task<IReadOnlyList<GpuDevice>> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HardwareSchedulingState scheduling = ReadHardwareSchedulingState();
        Dictionary<string, long> memoryByPnpId = ReadDedicatedMemoryByPnpId();
        var devices = new List<GpuDevice>();

        foreach (WmiRecord record in WmiQuery.Run(
            "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? pnpId = record.GetString("PNPDeviceID");
            if (string.IsNullOrWhiteSpace(pnpId))
            {
                continue;
            }

            GpuVendor vendor = ResolveVendor(pnpId);

            devices.Add(new GpuDevice
            {
                DeviceInstanceId = pnpId,
                Description = record.GetString("Name") ?? "Unknown display adapter",
                Vendor = vendor,
                PciDeviceId = ExtractToken(pnpId, "DEV_"),
                DriverVersion = record.GetString("DriverVersion"),
                DriverDate = ParseWmiDate(record.GetString("DriverDate")),
                DedicatedVideoMemoryBytes = ResolveMemory(memoryByPnpId, pnpId),
                IsIntegrated = IsIntegrated(pnpId, vendor),
                HardwareScheduling = scheduling,
            });
        }

        return Task.FromResult<IReadOnlyList<GpuDevice>>(devices);
    }

    private HardwareSchedulingState ReadHardwareSchedulingState()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath);

            // HwSchMode: 1 disabled, 2 enabled. Absence means the platform never offered it, which
            // is not the same as "supported and turned off", so it maps to Unknown.
            return key?.GetValue("HwSchMode") switch
            {
                2 => HardwareSchedulingState.Enabled,
                1 => HardwareSchedulingState.Disabled,
                _ => HardwareSchedulingState.Unknown,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the hardware scheduling state.");
            return HardwareSchedulingState.Unknown;
        }
    }

    private Dictionary<string, long> ReadDedicatedMemoryByPnpId()
    {
        var memory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey? displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (displayClass is null)
            {
                return memory;
            }

            foreach (string subKeyName in displayClass.GetSubKeyNames())
            {
                if (!subKeyName.All(char.IsDigit))
                {
                    continue;
                }

                using RegistryKey? adapter = displayClass.OpenSubKey(subKeyName);
                if (adapter?.GetValue("MatchingDeviceId") is not string matchingId)
                {
                    continue;
                }

                if (adapter.GetValue("HardwareInformation.qwMemorySize") is long size && size > 0)
                {
                    memory[matchingId] = size;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read adapter memory sizes from the display class key.");
        }

        return memory;
    }

    /// <summary>
    /// Matches an adapter's full Plug and Play instance path against the display class key.
    /// </summary>
    /// <remarks>
    /// <c>MatchingDeviceId</c> holds a prefix such as <c>PCI\VEN_10DE&amp;DEV_2504</c>, while
    /// <c>PNPDeviceID</c> additionally carries the subsystem, revision and instance. A prefix match
    /// is therefore the correct comparison; an equality check would silently find nothing.
    /// </remarks>
    private static long? ResolveMemory(Dictionary<string, long> memoryByPrefix, string pnpDeviceId)
    {
        foreach (KeyValuePair<string, long> candidate in memoryByPrefix)
        {
            if (pnpDeviceId.StartsWith(candidate.Key, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Value;
            }
        }

        return null;
    }

    private static GpuVendor ResolveVendor(string pnpDeviceId)
    {
        string? vendorId = ExtractToken(pnpDeviceId, "VEN_");

        return vendorId?.ToUpperInvariant() switch
        {
            "10DE" => GpuVendor.Nvidia,
            "1002" or "1022" => GpuVendor.Amd,
            "8086" => GpuVendor.Intel,
            null when pnpDeviceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) => GpuVendor.Microsoft,
            null => GpuVendor.Unknown,
            _ => GpuVendor.Other,
        };
    }

    private static bool IsIntegrated(string pnpDeviceId, GpuVendor vendor)
    {
        // Integrated adapters are not enumerated on the PCI Express bus with a discrete device
        // path; Intel iGPUs and AMD APUs appear under PCI with a vendor specific subsystem, so the
        // only robust signal available without D3D is the absence of dedicated memory plus vendor.
        return vendor == GpuVendor.Intel &&
               pnpDeviceId.Contains("PCI\\VEN_8086", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractToken(string deviceId, string prefix)
    {
        int start = deviceId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        int end = start;
        while (end < deviceId.Length && Uri.IsHexDigit(deviceId[end]))
        {
            end++;
        }

        return end > start ? deviceId[start..end] : null;
    }

    private static DateTimeOffset? ParseWmiDate(string? value)
    {
        // WMI CIM_DATETIME is yyyyMMddHHmmss.ffffff±UUU; only the date part is meaningful here.
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
