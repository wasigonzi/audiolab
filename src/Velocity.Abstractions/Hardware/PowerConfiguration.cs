using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>A Windows power scheme.</summary>
/// <param name="SchemeGuid">GUID identifying the scheme.</param>
/// <param name="Name">Friendly name of the scheme.</param>
/// <param name="IsActive">Whether the scheme is currently active.</param>
public readonly record struct PowerScheme(Guid SchemeGuid, string Name, bool IsActive);

/// <summary>
/// Processor power management settings for the active scheme, on AC power.
/// </summary>
/// <remarks>
/// Values are read through <c>PowerReadACValueIndex</c> for the documented
/// <c>GUID_PROCESSOR_SETTINGS_SUBGROUP</c> settings. Settings that a platform hides (core parking
/// on many modern parts) come back as null rather than being invented.
/// </remarks>
public sealed record ProcessorPowerPolicy
{
    /// <summary>Minimum processor state as a percentage, when exposed.</summary>
    public int? MinimumProcessorStatePercent { get; init; }

    /// <summary>Maximum processor state as a percentage, when exposed.</summary>
    public int? MaximumProcessorStatePercent { get; init; }

    /// <summary>Processor performance boost mode index, when exposed.</summary>
    public int? PerformanceBoostMode { get; init; }

    /// <summary>Minimum percentage of cores kept unparked, when core parking is exposed.</summary>
    public int? CoreParkingMinimumCoresPercent { get; init; }

    /// <summary>Maximum percentage of cores kept unparked, when core parking is exposed.</summary>
    public int? CoreParkingMaximumCoresPercent { get; init; }

    /// <summary>Whether the platform exposes core parking settings at all.</summary>
    public bool CoreParkingExposed => CoreParkingMinimumCoresPercent is not null;
}

/// <summary>Power configuration of the machine.</summary>
public sealed record PowerConfiguration
{
    /// <summary>The currently active power scheme.</summary>
    public required PowerScheme ActiveScheme { get; init; }

    /// <summary>All power schemes present on the machine.</summary>
    public IReadOnlyList<PowerScheme> AvailableSchemes { get; init; } = new List<PowerScheme>();

    /// <summary>Processor power policy of the active scheme on AC power.</summary>
    public ProcessorPowerPolicy ProcessorPolicy { get; init; } = new();

    /// <summary><see langword="true"/> when the machine is currently running on battery.</summary>
    public bool IsOnBattery { get; init; }

    /// <summary><see langword="true"/> when a battery is present, i.e. the machine is portable.</summary>
    public bool HasBattery { get; init; }
}
