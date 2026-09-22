using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>A display mode supported by a monitor.</summary>
/// <param name="Width">Horizontal resolution in pixels.</param>
/// <param name="Height">Vertical resolution in pixels.</param>
/// <param name="RefreshRateHz">Vertical refresh rate in hertz.</param>
public readonly record struct DisplayMode(int Width, int Height, int RefreshRateHz);

/// <summary>A monitor attached to the machine.</summary>
public sealed record DisplayDevice
{
    /// <summary>Windows display device name, for example <c>\\.\DISPLAY1</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Monitor friendly name when the EDID can be read.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Currently active display mode.</summary>
    public required DisplayMode CurrentMode { get; init; }

    /// <summary>Highest refresh rate available at the current resolution.</summary>
    public required int MaximumRefreshRateHzAtCurrentResolution { get; init; }

    /// <summary>Every mode the adapter enumerates for this monitor.</summary>
    public IReadOnlyList<DisplayMode> SupportedModes { get; init; } = new List<DisplayMode>();

    /// <summary><see langword="true"/> for the primary monitor.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// Whether variable refresh rate is active for this monitor, when Windows reports it.
    /// Null means "not determinable", which is not the same as "off".
    /// </summary>
    public bool? VariableRefreshRateActive { get; init; }

    /// <summary>
    /// <see langword="true"/> when the monitor supports a materially higher refresh rate at the
    /// current resolution than the one in use. This is the single most common real configuration
    /// defect on gaming machines.
    /// </summary>
    public bool IsRunningBelowMaximumRefreshRate =>
        MaximumRefreshRateHzAtCurrentResolution > CurrentMode.RefreshRateHz + 1;
}
