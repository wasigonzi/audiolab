using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>Physical media class of a storage device.</summary>
public enum StorageMediaType
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Rotational hard disk.</summary>
    HardDisk = 1,

    /// <summary>Solid state drive.</summary>
    SolidState = 2,

    /// <summary>Storage class memory.</summary>
    StorageClassMemory = 3,
}

/// <summary>Bus a storage device is attached to.</summary>
public enum StorageBusType
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>SATA.</summary>
    Sata = 1,

    /// <summary>NVMe over PCIe.</summary>
    Nvme = 2,

    /// <summary>USB attached storage.</summary>
    Usb = 3,

    /// <summary>SAS/SCSI.</summary>
    Scsi = 4,

    /// <summary>RAID controller.</summary>
    Raid = 5,
}

/// <summary>A mounted volume on a storage device.</summary>
public sealed record StorageVolume
{
    /// <summary>Drive letter including the colon, for example <c>C:</c>. Null for unlettered volumes.</summary>
    public string? DriveLetter { get; init; }

    /// <summary>Volume label.</summary>
    public string? Label { get; init; }

    /// <summary>File system name, for example <c>NTFS</c>.</summary>
    public string? FileSystem { get; init; }

    /// <summary>Total volume size in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Free space in bytes.</summary>
    public long FreeBytes { get; init; }
}

/// <summary>A physical storage device.</summary>
public sealed record StorageDevice
{
    /// <summary>Stable device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Model string reported by the device.</summary>
    public required string Model { get; init; }

    /// <summary>Media class.</summary>
    public required StorageMediaType MediaType { get; init; }

    /// <summary>Bus the device is attached to.</summary>
    public required StorageBusType BusType { get; init; }

    /// <summary>Device capacity in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary><see langword="true"/> when the Windows installation lives on this device.</summary>
    public bool HostsSystemVolume { get; init; }

    /// <summary>Volumes mounted from this device.</summary>
    public IReadOnlyList<StorageVolume> Volumes { get; init; } = new List<StorageVolume>();
}
