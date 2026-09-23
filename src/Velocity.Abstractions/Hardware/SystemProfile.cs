using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// Everything the optimizer knows about the machine at a point in time.
/// </summary>
/// <remarks>
/// A profile is captured once at startup, refreshed on demand, and stored alongside every
/// benchmark result. Optimization results are only ever compared between runs whose
/// <see cref="Fingerprint"/> matches, because a result measured on different hardware, a
/// different Windows build or a different driver is not evidence about this machine.
/// </remarks>
public sealed record SystemProfile
{
    /// <summary>When the profile was captured.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Operating system identity.</summary>
    public required OperatingSystemInfo OperatingSystem { get; init; }

    /// <summary>Chassis class of the machine.</summary>
    public required MachineKind MachineKind { get; init; }

    /// <summary>Processor topology.</summary>
    public required CpuTopology Cpu { get; init; }

    /// <summary>Memory state.</summary>
    public required MemoryInfo Memory { get; init; }

    /// <summary>Installed display adapters.</summary>
    public IReadOnlyList<GpuDevice> Gpus { get; init; } = new List<GpuDevice>();

    /// <summary>Physical storage devices.</summary>
    public IReadOnlyList<StorageDevice> StorageDevices { get; init; } = new List<StorageDevice>();

    /// <summary>Network adapters.</summary>
    public IReadOnlyList<NetworkAdapter> NetworkAdapters { get; init; } = new List<NetworkAdapter>();

    /// <summary>Attached monitors.</summary>
    public IReadOnlyList<DisplayDevice> Displays { get; init; } = new List<DisplayDevice>();

    /// <summary>Power configuration.</summary>
    public required PowerConfiguration Power { get; init; }

    /// <summary>Security and virtualization feature state.</summary>
    public PlatformSecurityInfo PlatformSecurity { get; init; } = new();

    /// <summary>Stable identity of this hardware and software combination.</summary>
    public required HardwareFingerprint Fingerprint { get; init; }

    /// <summary>
    /// Probes that failed, keyed by probe name, with the reason. A failed probe degrades the
    /// profile rather than failing the application, but the failure is never hidden: modules that
    /// depend on missing data report themselves as incompatible instead of guessing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProbeFailures { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
