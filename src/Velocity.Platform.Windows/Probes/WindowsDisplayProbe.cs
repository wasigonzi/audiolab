using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Enumerates attached monitors, their current mode and the modes they support.</summary>
/// <remarks>
/// The supported mode list is what makes the "your 240 Hz monitor is running at 60 Hz" warning
/// possible, which is the single most common real configuration defect on gaming machines and
/// costs nothing to detect.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayProbe : IDisplayProbe
{
    /// <inheritdoc />
    public string ProbeName => "display";

    /// <inheritdoc />
    public Task<IReadOnlyList<DisplayDevice>> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var displays = new List<DisplayDevice>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var adapter = new NativeMethods.DisplayDeviceW
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.DisplayDeviceW>(),
            };

            if (!NativeMethods.EnumDisplayDevicesW(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) == 0)
            {
                continue;
            }

            DisplayMode? current = ReadCurrentMode(adapter.DeviceName);
            if (current is null)
            {
                continue;
            }

            List<DisplayMode> supported = ReadSupportedModes(adapter.DeviceName);
            int maximumAtCurrentResolution = supported
                .Where(mode => mode.Width == current.Value.Width && mode.Height == current.Value.Height)
                .Select(mode => mode.RefreshRateHz)
                .DefaultIfEmpty(current.Value.RefreshRateHz)
                .Max();

            displays.Add(new DisplayDevice
            {
                DeviceName = adapter.DeviceName,
                FriendlyName = ReadMonitorName(adapter.DeviceName) ?? adapter.DeviceString,
                CurrentMode = current.Value,
                MaximumRefreshRateHzAtCurrentResolution = maximumAtCurrentResolution,
                SupportedModes = supported,
                IsPrimary = (adapter.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0,

                // Windows exposes no documented user mode API for the live variable refresh rate
                // state, so it is reported as unknown rather than guessed from the driver name.
                VariableRefreshRateActive = null,
            });
        }

        return Task.FromResult<IReadOnlyList<DisplayDevice>>(displays);
    }

    private static DisplayMode? ReadCurrentMode(string deviceName)
    {
        var mode = new NativeMethods.DevModeW
        {
            Size = (ushort)Marshal.SizeOf<NativeMethods.DevModeW>(),
        };

        return NativeMethods.EnumDisplaySettingsExW(deviceName, NativeMethods.EnumCurrentSettings, ref mode, 0)
            ? new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency)
            : null;
    }

    private static List<DisplayMode> ReadSupportedModes(string deviceName)
    {
        var modes = new List<DisplayMode>();

        for (int index = 0; ; index++)
        {
            var mode = new NativeMethods.DevModeW
            {
                Size = (ushort)Marshal.SizeOf<NativeMethods.DevModeW>(),
            };

            if (!NativeMethods.EnumDisplaySettingsExW(deviceName, index, ref mode, 0))
            {
                break;
            }

            var candidate = new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency);
            if (!modes.Contains(candidate))
            {
                modes.Add(candidate);
            }
        }

        return modes;
    }

    private static string? ReadMonitorName(string adapterName)
    {
        var monitor = new NativeMethods.DisplayDeviceW
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.DisplayDeviceW>(),
        };

        return NativeMethods.EnumDisplayDevicesW(adapterName, 0, ref monitor, 0)
            ? monitor.DeviceString
            : null;
    }
}
