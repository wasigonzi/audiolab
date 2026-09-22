using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Enumerates physical storage devices and the volumes mounted from them.</summary>
/// <remarks>
/// <para>
/// Media and bus type come from <c>MSFT_PhysicalDisk</c> in the storage management namespace. The
/// older <c>Win32_DiskDrive</c> class cannot distinguish an NVMe SSD from a SATA SSD, and the
/// difference decides whether a storage recommendation makes sense at all.
/// </para>
/// <para>
/// Knowing which device is solid state is also what keeps the product from ever offering a
/// defragmentation pass on an SSD.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStorageProbe : IStorageProbe
{
    private readonly ILogger<WindowsStorageProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsStorageProbe(ILogger<WindowsStorageProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "storage";

    /// <inheritdoc />
    public Task<IReadOnlyList<StorageDevice>> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, List<StorageVolume>> volumesByDiskNumber = MapVolumesToDisks();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var devices = new List<StorageDevice>();

        foreach (WmiRecord record in QueryPhysicalDisks())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string deviceId = record.GetString("DeviceId") ?? record.GetString("DeviceID") ?? string.Empty;
            List<StorageVolume> volumes = volumesByDiskNumber.TryGetValue(deviceId, out List<StorageVolume>? mapped)
                ? mapped
                : new List<StorageVolume>();

            devices.Add(new StorageDevice
            {
                DeviceId = deviceId,
                Model = record.GetString("FriendlyName") ?? record.GetString("Model") ?? "Unknown device",
                MediaType = MapMediaType(record.GetUInt32("MediaType")),
                BusType = MapBusType(record.GetUInt32("BusType")),
                SizeBytes = (long)(record.GetUInt64("Size") ?? 0UL),
                HostsSystemVolume = volumes.Exists(volume =>
                    volume.DriveLetter is not null &&
                    systemRoot.StartsWith(volume.DriveLetter, StringComparison.OrdinalIgnoreCase)),
                Volumes = volumes,
            });
        }

        return Task.FromResult<IReadOnlyList<StorageDevice>>(devices);
    }

    private IReadOnlyList<WmiRecord> QueryPhysicalDisks()
    {
        try
        {
            return WmiQuery.Run(
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk",
                WmiQuery.StorageNamespace);
        }
        catch (Exception ex)
        {
            // The storage management namespace is absent on some stripped images; fall back to the
            // legacy class and accept that media type will read as unknown.
            _logger.LogDebug(ex, "MSFT_PhysicalDisk is unavailable; falling back to Win32_DiskDrive.");
            return WmiQuery.Run("SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive");
        }
    }

    private Dictionary<string, List<StorageVolume>> MapVolumesToDisks()
    {
        var map = new Dictionary<string, List<StorageVolume>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Win32_DiskDriveToDiskPartition and Win32_LogicalDiskToPartition are the documented
            // path from a physical disk to a drive letter.
            var partitionToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (WmiRecord association in WmiQuery.Run(
                "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            {
                string? disk = ExtractDeviceId(association.GetString("Antecedent"));
                string? partition = ExtractDeviceId(association.GetString("Dependent"));

                if (disk is not null && partition is not null)
                {
                    partitionToDisk[partition] = ExtractDiskNumber(disk);
                }
            }

            foreach (WmiRecord association in WmiQuery.Run(
                "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                string? partition = ExtractDeviceId(association.GetString("Antecedent"));
                string? logicalDisk = ExtractDeviceId(association.GetString("Dependent"));

                if (partition is null || logicalDisk is null ||
                    !partitionToDisk.TryGetValue(partition, out string? diskNumber))
                {
                    continue;
                }

                StorageVolume volume = ReadVolume(logicalDisk);

                if (!map.TryGetValue(diskNumber, out List<StorageVolume>? volumes))
                {
                    volumes = new List<StorageVolume>();
                    map[diskNumber] = volumes;
                }

                volumes.Add(volume);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not map volumes to physical disks.");
        }

        return map;
    }

    private static StorageVolume ReadVolume(string driveLetter)
    {
        try
        {
            var info = new DriveInfo(driveLetter);
            return new StorageVolume
            {
                DriveLetter = driveLetter,
                Label = info.IsReady ? info.VolumeLabel : null,
                FileSystem = info.IsReady ? info.DriveFormat : null,
                TotalBytes = info.IsReady ? info.TotalSize : 0,
                FreeBytes = info.IsReady ? info.AvailableFreeSpace : 0,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new StorageVolume { DriveLetter = driveLetter };
        }
    }

    private static string? ExtractDeviceId(string? associationPath)
    {
        // Association paths look like: \\MACHINE\root\cimv2:Win32_DiskPartition.DeviceID="Disk #0, Partition #1"
        if (string.IsNullOrWhiteSpace(associationPath))
        {
            return null;
        }

        int start = associationPath.IndexOf('"', StringComparison.Ordinal);
        int end = associationPath.LastIndexOf('"');
        return start >= 0 && end > start ? associationPath[(start + 1)..end] : null;
    }

    private static string ExtractDiskNumber(string diskDeviceId)
    {
        // \\.\PHYSICALDRIVE0 -> 0, which is the DeviceId MSFT_PhysicalDisk reports.
        int index = diskDeviceId.LastIndexOfAny(['E', 'e']);
        string tail = index >= 0 && index + 1 < diskDeviceId.Length ? diskDeviceId[(index + 1)..] : diskDeviceId;
        return tail.Trim();
    }

    private static StorageMediaType MapMediaType(uint? value) => value switch
    {
        3 => StorageMediaType.HardDisk,
        4 => StorageMediaType.SolidState,
        5 => StorageMediaType.StorageClassMemory,
        _ => StorageMediaType.Unknown,
    };

    private static StorageBusType MapBusType(uint? value) => value switch
    {
        7 => StorageBusType.Usb,
        8 => StorageBusType.Raid,
        10 => StorageBusType.Scsi,
        11 => StorageBusType.Sata,
        17 => StorageBusType.Nvme,
        _ => StorageBusType.Unknown,
    };
}
