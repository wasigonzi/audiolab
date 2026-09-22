using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Power;

/// <summary>Reads and writes power scheme configuration.</summary>
/// <remarks>
/// Only AC values are written. Writing the DC side would change what happens on battery, which is
/// a decision about battery life that a gaming optimizer has no business making silently.
/// </remarks>
public interface IPowerConfigurationController
{
    /// <summary>Returns the GUID of the active power scheme.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The scheme GUID, or <see cref="Guid.Empty"/> when it cannot be read.</returns>
    Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken);

    /// <summary>Activates a power scheme.</summary>
    /// <param name="schemeGuid">Scheme to activate.</param>
    /// <param name="cancellationToken">Token used to abort the change.</param>
    /// <returns><see langword="true"/> when the scheme became active.</returns>
    Task<bool> SetActiveSchemeAsync(Guid schemeGuid, CancellationToken cancellationToken);

    /// <summary>Reads a setting's AC value index in a scheme.</summary>
    /// <param name="schemeGuid">Scheme to read from.</param>
    /// <param name="subgroupGuid">Setting subgroup.</param>
    /// <param name="settingGuid">Setting.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The value, or <see langword="null"/> when the platform hides the setting.</returns>
    Task<uint?> ReadAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        CancellationToken cancellationToken);

    /// <summary>Writes a setting's AC value index in a scheme.</summary>
    /// <param name="schemeGuid">Scheme to write to.</param>
    /// <param name="subgroupGuid">Setting subgroup.</param>
    /// <param name="settingGuid">Setting.</param>
    /// <param name="value">Value index to write.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns><see langword="true"/> when the value was written.</returns>
    Task<bool> WriteAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        uint value,
        CancellationToken cancellationToken);
}

/// <summary>The documented power setting GUIDs this product uses.</summary>
/// <remarks>
/// Only settings with published semantics appear here. A setting whose meaning is folklore is not
/// something this product writes.
/// </remarks>
public static class PowerSettings
{
    /// <summary>The Balanced scheme, which is the Windows default.</summary>
    public static Guid BalancedScheme { get; } = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>The High performance scheme.</summary>
    public static Guid HighPerformanceScheme { get; } = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>The Ultimate performance scheme, present only on some editions.</summary>
    public static Guid UltimatePerformanceScheme { get; } = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Processor power management subgroup.</summary>
    public static Guid ProcessorSubgroup { get; } = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Minimum processor state, as a percentage.</summary>
    public static Guid MinimumProcessorState { get; } = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    /// <summary>Maximum processor state, as a percentage.</summary>
    public static Guid MaximumProcessorState { get; } = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    /// <summary>Processor performance boost mode.</summary>
    public static Guid PerformanceBoostMode { get; } = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>PCI Express link state power management subgroup.</summary>
    public static Guid PciExpressSubgroup { get; } = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    /// <summary>Link state power management for PCI Express.</summary>
    public static Guid PciExpressLinkStatePowerManagement { get; } =
        new("ee12f906-d277-404b-b6da-e5fa1a576df5");
}
