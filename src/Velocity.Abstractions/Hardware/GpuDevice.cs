using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>Vendor of a display adapter.</summary>
public enum GpuVendor
{
    /// <summary>Vendor could not be determined from the PCI vendor id.</summary>
    Unknown = 0,

    /// <summary>NVIDIA (PCI vendor 0x10DE).</summary>
    Nvidia = 1,

    /// <summary>AMD/ATI (PCI vendor 0x1002).</summary>
    Amd = 2,

    /// <summary>Intel (PCI vendor 0x8086).</summary>
    Intel = 3,

    /// <summary>Microsoft software adapter (Basic Render Driver, WARP).</summary>
    Microsoft = 4,

    /// <summary>A vendor recognised by id but not specifically supported.</summary>
    Other = 99,
}

/// <summary>State of Hardware Accelerated GPU Scheduling for an adapter.</summary>
/// <remarks>
/// HAGS is controlled by <c>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers!HwSchMode</c>
/// and requires both driver and hardware support. Windows reports support through
/// <c>DXGK_FEATURE_SUPPORT</c>; the value alone does not tell you whether it is in effect.
/// </remarks>
public enum HardwareSchedulingState
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>The adapter or driver does not support hardware scheduling.</summary>
    NotSupported = 1,

    /// <summary>Supported and currently disabled.</summary>
    Disabled = 2,

    /// <summary>Supported and currently enabled.</summary>
    Enabled = 3,
}

/// <summary>A display adapter installed in the machine.</summary>
public sealed record GpuDevice
{
    /// <summary>Plug and Play device instance path, used as the stable identity of the adapter.</summary>
    public required string DeviceInstanceId { get; init; }

    /// <summary>Adapter description as reported by the driver.</summary>
    public required string Description { get; init; }

    /// <summary>Vendor resolved from the PCI vendor id in the device instance path.</summary>
    public required GpuVendor Vendor { get; init; }

    /// <summary>PCI device id, when it can be parsed from the device instance path.</summary>
    public string? PciDeviceId { get; init; }

    /// <summary>Installed driver version string.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Installed driver date, if reported.</summary>
    public System.DateTimeOffset? DriverDate { get; init; }

    /// <summary>Dedicated video memory in bytes, when reported.</summary>
    public long? DedicatedVideoMemoryBytes { get; init; }

    /// <summary><see langword="true"/> when the adapter is integrated into the CPU package.</summary>
    public bool IsIntegrated { get; init; }

    /// <summary>State of Hardware Accelerated GPU Scheduling for this adapter.</summary>
    public HardwareSchedulingState HardwareScheduling { get; init; }

    /// <summary>Display device names attached to this adapter.</summary>
    public IReadOnlyList<string> AttachedDisplays { get; init; } = new List<string>();
}
