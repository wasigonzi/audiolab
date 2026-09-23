using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Power;

namespace Velocity.TestSupport;

/// <summary>
/// An in-memory stand-in for the Windows power configuration API.
/// </summary>
/// <remarks>
/// Only the settings explicitly seeded through <see cref="Expose"/> are readable. Everything else
/// reads as <see langword="null"/>, which is how the real API behaves for a setting the platform
/// hides, and is the case the snapshot layer has to get right.
/// </remarks>
public sealed class FakePowerConfigurationController : IPowerConfigurationController
{
    private readonly Dictionary<(Guid Scheme, Guid Subgroup, Guid Setting), uint> _values = new();

    /// <summary>Creates the controller.</summary>
    /// <param name="activeScheme">Scheme to report as active.</param>
    public FakePowerConfigurationController(Guid activeScheme) => ActiveScheme = activeScheme;

    /// <summary>The scheme currently reported as active.</summary>
    public Guid ActiveScheme { get; private set; }

    /// <summary>Schemes that exist; activating anything else fails, as Windows does.</summary>
    public HashSet<Guid> KnownSchemes { get; } = [];

    /// <summary>Number of times a scheme was activated.</summary>
    public int ActivationCount { get; private set; }

    /// <summary>Settings written, in order, so a test can assert the DC side stayed untouched.</summary>
    public List<(Guid Scheme, Guid Subgroup, Guid Setting, uint Value)> Writes { get; } = [];

    /// <summary>Makes a setting readable with the given value.</summary>
    /// <param name="scheme">Scheme holding the setting.</param>
    /// <param name="subgroup">Setting subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="value">Value to report.</param>
    public void Expose(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        _values[(scheme, subgroup, setting)] = value;

    /// <inheritdoc />
    public Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ActiveScheme);

    /// <inheritdoc />
    public Task<bool> SetActiveSchemeAsync(Guid schemeGuid, CancellationToken cancellationToken)
    {
        if (KnownSchemes.Count > 0 && !KnownSchemes.Contains(schemeGuid))
        {
            return Task.FromResult(false);
        }

        ActiveScheme = schemeGuid;
        ActivationCount++;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<uint?> ReadAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        CancellationToken cancellationToken) =>
        Task.FromResult<uint?>(_values.TryGetValue((schemeGuid, subgroupGuid, settingGuid), out uint value)
            ? value
            : null);

    /// <inheritdoc />
    public Task<bool> WriteAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        uint value,
        CancellationToken cancellationToken)
    {
        _values[(schemeGuid, subgroupGuid, settingGuid)] = value;
        Writes.Add((schemeGuid, subgroupGuid, settingGuid, value));
        return Task.FromResult(true);
    }
}
