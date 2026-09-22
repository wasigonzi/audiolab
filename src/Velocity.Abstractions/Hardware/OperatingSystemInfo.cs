using System;

namespace Velocity.Abstractions.Hardware;

/// <summary>Chassis class of the machine, which changes which optimizations are appropriate.</summary>
public enum MachineKind
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Desktop or tower.</summary>
    Desktop = 1,

    /// <summary>Laptop or notebook.</summary>
    Laptop = 2,

    /// <summary>Handheld gaming device.</summary>
    Handheld = 3,

    /// <summary>Virtual machine.</summary>
    VirtualMachine = 4,
}

/// <summary>Operating system identity and build information.</summary>
public sealed record OperatingSystemInfo
{
    /// <summary>Product name, for example <c>Windows 11 Pro</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>Feature update label, for example <c>24H2</c>.</summary>
    public string? DisplayVersion { get; init; }

    /// <summary>Major OS version.</summary>
    public required int MajorVersion { get; init; }

    /// <summary>Minor OS version.</summary>
    public required int MinorVersion { get; init; }

    /// <summary>OS build number, for example <c>26100</c>.</summary>
    public required int BuildNumber { get; init; }

    /// <summary>Update build revision (the part after the build number).</summary>
    public int UpdateBuildRevision { get; init; }

    /// <summary>Process and OS architecture, for example <c>X64</c> or <c>Arm64</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary>Time since the machine last booted.</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>
    /// A comparable build value of the form <c>build.revision</c> used by compatibility checks.
    /// </summary>
    public long ComparableBuild => ((long)BuildNumber * 100_000) + UpdateBuildRevision;
}

/// <summary>
/// Security and virtualization features that measurably affect game performance.
/// </summary>
/// <remarks>
/// These are reported, never silently changed. Virtualization Based Security and memory integrity
/// do cost measurable performance on some workloads, and users are entitled to know their state,
/// but turning them off is a security decision that belongs to the user and is surfaced with its
/// consequences rather than bundled into an "optimize" button.
/// </remarks>
public sealed record PlatformSecurityInfo
{
    /// <summary>Whether a hypervisor is present (Hyper-V, VBS, WSL2, sandbox, ...).</summary>
    public bool? HypervisorPresent { get; init; }

    /// <summary>Whether Virtualization Based Security is running.</summary>
    public bool? VirtualizationBasedSecurityRunning { get; init; }

    /// <summary>Whether HVCI (memory integrity) is running.</summary>
    public bool? MemoryIntegrityRunning { get; init; }

    /// <summary>Whether Core Isolation is available on the platform.</summary>
    public bool? CoreIsolationAvailable { get; init; }

    /// <summary>Whether Windows Game Mode is enabled for the current user.</summary>
    public bool? GameModeEnabled { get; init; }

    /// <summary>Whether Game Bar / Game DVR background recording is enabled for the current user.</summary>
    public bool? GameDvrEnabled { get; init; }
}
