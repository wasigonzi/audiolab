using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>
/// Determines whether the machine is a desktop, a laptop, a handheld or a virtual machine.
/// </summary>
/// <remarks>
/// The chassis class is not cosmetic. Power and core parking recommendations that are reasonable
/// on a desktop are actively wrong on a battery powered handheld, and several modules refuse to
/// run at all inside a virtual machine because the measurements would be meaningless.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineKindProbe : IMachineKindProbe
{
    // SMBIOS chassis types, from the DMTF specification.
    private static readonly ushort[] LaptopChassisTypes = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
    private static readonly ushort[] DesktopChassisTypes = [3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24];

    private readonly ILogger<WindowsMachineKindProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsMachineKindProbe(ILogger<WindowsMachineKindProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "machine-kind";

    /// <inheritdoc />
    public Task<MachineKind> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (IsVirtualMachine())
            {
                return Task.FromResult(MachineKind.VirtualMachine);
            }

            IReadOnlyList<WmiRecord> enclosures =
                WmiQuery.Run("SELECT ChassisTypes FROM Win32_SystemEnclosure");

            foreach (WmiRecord enclosure in enclosures)
            {
                foreach (ushort chassisType in enclosure.GetUInt16Array("ChassisTypes"))
                {
                    if (LaptopChassisTypes.Contains(chassisType))
                    {
                        return Task.FromResult(MachineKind.Laptop);
                    }

                    if (DesktopChassisTypes.Contains(chassisType))
                    {
                        return Task.FromResult(MachineKind.Desktop);
                    }
                }
            }

            return Task.FromResult(MachineKind.Unknown);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine the chassis type.");
            return Task.FromResult(MachineKind.Unknown);
        }
    }

    private static bool IsVirtualMachine()
    {
        IReadOnlyList<WmiRecord> systems =
            WmiQuery.Run("SELECT Manufacturer, Model FROM Win32_ComputerSystem");

        if (systems.Count == 0)
        {
            return false;
        }

        string manufacturer = systems[0].GetString("Manufacturer") ?? string.Empty;
        string model = systems[0].GetString("Model") ?? string.Empty;

        string[] markers = ["VMware", "VirtualBox", "Virtual Machine", "QEMU", "KVM", "Xen", "Parallels", "Hyper-V"];
        return markers.Any(marker =>
            manufacturer.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            model.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
