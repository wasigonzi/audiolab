using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>
/// Reports the state of the security and virtualization features that measurably affect games.
/// </summary>
/// <remarks>
/// This probe exists so the product can be honest about performance relevant security settings.
/// It reads them and never changes them: virtualization based security and memory integrity do
/// cost measurable performance on some workloads, and the user is entitled to know that, but
/// turning them off is a security decision that belongs to them and is made in Windows.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformSecurityProbe : IPlatformSecurityProbe
{
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameConfigStorePath = @"System\GameConfigStore";

    private readonly ILogger<WindowsPlatformSecurityProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsPlatformSecurityProbe(ILogger<WindowsPlatformSecurityProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "platform-security";

    /// <inheritdoc />
    public Task<PlatformSecurityInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (bool? vbsRunning, bool? memoryIntegrity, bool? available) = ReadDeviceGuardState();

        return Task.FromResult(new PlatformSecurityInfo
        {
            HypervisorPresent = ReadHypervisorPresent(),
            VirtualizationBasedSecurityRunning = vbsRunning,
            MemoryIntegrityRunning = memoryIntegrity,
            CoreIsolationAvailable = available,
            GameModeEnabled = ReadCurrentUserFlag(GameBarPath, "AutoGameModeEnabled"),
            GameDvrEnabled = ReadCurrentUserFlag(GameConfigStorePath, "GameDVR_Enabled"),
        });
    }

    private (bool? VbsRunning, bool? MemoryIntegrity, bool? Available) ReadDeviceGuardState()
    {
        try
        {
            IReadOnlyList<WmiRecord> records = WmiQuery.Run(
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning " +
                "FROM Win32_DeviceGuard",
                WmiQuery.DeviceGuardNamespace);

            if (records.Count == 0)
            {
                return (null, null, null);
            }

            WmiRecord record = records[0];

            // VirtualizationBasedSecurityStatus: 0 off, 1 configured but not running, 2 running.
            uint? status = record.GetUInt32("VirtualizationBasedSecurityStatus");

            // SecurityServicesRunning contains 1 for credential guard and 2 for HVCI.
            IReadOnlyList<ushort> running = record.GetUInt16Array("SecurityServicesRunning");

            return (
                status is null ? null : status == 2,
                running.Count == 0 ? null : running.Contains((ushort)2),
                status is not null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device Guard state is unavailable on this edition.");
            return (null, null, null);
        }
    }

    private bool? ReadHypervisorPresent()
    {
        try
        {
            IReadOnlyList<WmiRecord> records =
                WmiQuery.Run("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            return records.Count == 0 ? null : records[0].GetBoolean("HypervisorPresent");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine whether a hypervisor is present.");
            return null;
        }
    }

    private bool? ReadCurrentUserFlag(string path, string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(valueName) is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read {Path}!{Value}.", path, valueName);
            return null;
        }
    }
}
