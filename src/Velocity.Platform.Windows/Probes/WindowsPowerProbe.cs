using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Reads power schemes and the processor power policy of the active scheme.</summary>
/// <remarks>
/// <para>
/// Processor settings are read on the AC side only. Reporting the DC values next to them would
/// invite a laptop user to conclude that a change had been applied when it had not, because
/// Windows switches value sets when the power source changes.
/// </para>
/// <para>
/// A setting the platform hides, which is what happens to core parking on most modern desktops,
/// comes back as <see langword="null"/> rather than as a fabricated default. A module that needs it
/// then reports itself incompatible instead of writing a value the firmware ignores.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerProbe : IPowerProbe
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MinimumProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid MaximumProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid PerformanceBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid CoreParkingMinimumCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid CoreParkingMaximumCores = new("ea062031-0e34-4ff1-9b6d-eb1059334028");

    private readonly ILogger<WindowsPowerProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsPowerProbe(ILogger<WindowsPowerProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "power";

    /// <inheritdoc />
    public Task<PowerConfiguration> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Guid activeGuid = GetActiveSchemeGuid();
        var schemes = new List<PowerScheme>();

        foreach (Guid schemeGuid in EnumerateSchemes())
        {
            schemes.Add(new PowerScheme(schemeGuid, ReadFriendlyName(schemeGuid), schemeGuid == activeGuid));
        }

        var active = new PowerScheme(activeGuid, ReadFriendlyName(activeGuid), IsActive: true);

        bool onBattery = false;
        bool hasBattery = false;

        if (NativeMethods.GetSystemPowerStatus(out NativeMethods.SystemPowerStatus status))
        {
            hasBattery = (status.BatteryFlag & NativeMethods.BatteryFlagNoSystemBattery) == 0;
            onBattery = hasBattery && status.AcLineStatus == NativeMethods.AcLineStatusOffline;
        }

        return Task.FromResult(new PowerConfiguration
        {
            ActiveScheme = active,
            AvailableSchemes = schemes,
            ProcessorPolicy = ReadProcessorPolicy(activeGuid),
            IsOnBattery = onBattery,
            HasBattery = hasBattery,
        });
    }

    private ProcessorPowerPolicy ReadProcessorPolicy(Guid scheme) => new()
    {
        MinimumProcessorStatePercent = ReadAcValue(scheme, MinimumProcessorState),
        MaximumProcessorStatePercent = ReadAcValue(scheme, MaximumProcessorState),
        PerformanceBoostMode = ReadAcValue(scheme, PerformanceBoostMode),
        CoreParkingMinimumCoresPercent = ReadAcValue(scheme, CoreParkingMinimumCores),
        CoreParkingMaximumCoresPercent = ReadAcValue(scheme, CoreParkingMaximumCores),
    };

    private int? ReadAcValue(Guid scheme, Guid setting)
    {
        uint result = NativeMethods.PowerReadACValueIndex(
            IntPtr.Zero, in scheme, in ProcessorSubgroup, in setting, out uint value);

        if (result == NativeMethods.ErrorSuccess)
        {
            return (int)value;
        }

        _logger.LogDebug(
            "Power setting {Setting} is not exposed on this platform (error {Error}).", setting, result);
        return null;
    }

    private static Guid GetActiveSchemeGuid()
    {
        uint result = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);

        if (result != NativeMethods.ErrorSuccess || pointer == IntPtr.Zero)
        {
            return Guid.Empty;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            NativeMethods.LocalFree(pointer);
        }
    }

    private static IEnumerable<Guid> EnumerateSchemes()
    {
        var schemes = new List<Guid>();
        int guidSize = Marshal.SizeOf<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal(guidSize);

        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = (uint)guidSize;
                uint result = NativeMethods.PowerEnumerate(
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.AccessScheme, index, buffer, ref size);

                if (result != NativeMethods.ErrorSuccess)
                {
                    break;
                }

                schemes.Add(Marshal.PtrToStructure<Guid>(buffer));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return schemes;
    }

    private static string ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        NativeMethods.PowerReadFriendlyName(
            IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);

        if (size == 0)
        {
            return scheme.ToString();
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = NativeMethods.PowerReadFriendlyName(
                IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);

            return result == NativeMethods.ErrorSuccess
                ? Marshal.PtrToStringUni(buffer) ?? scheme.ToString()
                : scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
