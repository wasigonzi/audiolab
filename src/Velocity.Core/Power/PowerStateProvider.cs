using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Power;
using Velocity.Abstractions.State;

namespace Velocity.Core.Power;

/// <summary>
/// Exposes power configuration to the snapshot and rollback engines.
/// </summary>
/// <remarks>
/// <para>
/// Two key shapes are supported:
/// </para>
/// <list type="bullet">
/// <item><c>power://active-scheme</c> — the GUID of the active scheme.</item>
/// <item><c>power://&lt;scheme&gt;/&lt;subgroup&gt;/&lt;setting&gt;#ac</c> — one AC value index.</item>
/// </list>
/// <para>
/// A setting the platform hides reads as absent rather than zero, so restoring it does nothing
/// rather than writing a value the firmware never had. That distinction matters most for core
/// parking, which most modern desktops do not expose at all.
/// </para>
/// </remarks>
public sealed class PowerStateProvider : IStateProvider
{
    /// <summary>Scheme this provider answers for.</summary>
    public const string Scheme = "power";

    /// <summary>Path identifying the active power scheme.</summary>
    public const string ActiveSchemePath = "active-scheme";

    /// <summary>Item name for an AC side value.</summary>
    public const string AcItem = "ac";

    private readonly IPowerConfigurationController _controller;
    private readonly ILogger<PowerStateProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="controller">Reads and writes power configuration.</param>
    /// <param name="logger">Logger.</param>
    public PowerStateProvider(
        IPowerConfigurationController controller,
        ILogger<PowerStateProvider> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => Scheme;

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public bool RequiresElevation(StateKey key) => true;

    /// <summary>Builds the key identifying the active power scheme.</summary>
    /// <returns>The key.</returns>
    public static StateKey ActiveSchemeKey() => new(Scheme, ActiveSchemePath, null);

    /// <summary>Builds the key identifying one AC setting value.</summary>
    /// <param name="schemeGuid">Scheme holding the setting.</param>
    /// <param name="subgroupGuid">Setting subgroup.</param>
    /// <param name="settingGuid">Setting.</param>
    /// <returns>The key.</returns>
    public static StateKey SettingKey(Guid schemeGuid, Guid subgroupGuid, Guid settingGuid) =>
        new(Scheme, $"{schemeGuid:D}/{subgroupGuid:D}/{settingGuid:D}", AcItem);

    /// <inheritdoc />
    public async Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken)
    {
        if (IsActiveScheme(key))
        {
            Guid active = await _controller.GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
            return active == Guid.Empty
                ? StateValue.Absent
                : StateValue.FromString(active.ToString("D", CultureInfo.InvariantCulture));
        }

        (Guid scheme, Guid subgroup, Guid setting) = ParseSettingPath(key);

        uint? value = await _controller
            .ReadAcValueAsync(scheme, subgroup, setting, cancellationToken)
            .ConfigureAwait(false);

        // A hidden setting is absent, not zero: restoring it must not write a value the firmware
        // never had.
        return value is null ? StateValue.Absent : StateValue.FromUInt32(value.Value);
    }

    /// <inheritdoc />
    public async Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        if (value.IsAbsent)
        {
            _logger.LogDebug("Skipping restore of {Key}: it was not exposed when captured.", key.ToString());
            return;
        }

        if (IsActiveScheme(key))
        {
            if (Guid.TryParse(value.Data, out Guid scheme))
            {
                await _controller.SetActiveSchemeAsync(scheme, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        (Guid schemeGuid, Guid subgroup, Guid setting) = ParseSettingPath(key);
        uint? numeric = value.AsUInt32();

        if (numeric is null)
        {
            _logger.LogWarning("Ignoring non numeric power value for {Key}.", key.ToString());
            return;
        }

        await _controller
            .WriteAcValueAsync(schemeGuid, subgroup, setting, numeric.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsActiveScheme(StateKey key) =>
        string.Equals(key.Path, ActiveSchemePath, StringComparison.OrdinalIgnoreCase);

    private static (Guid Scheme, Guid Subgroup, Guid Setting) ParseSettingPath(StateKey key)
    {
        string[] parts = key.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3 ||
            !Guid.TryParse(parts[0], out Guid scheme) ||
            !Guid.TryParse(parts[1], out Guid subgroup) ||
            !Guid.TryParse(parts[2], out Guid setting))
        {
            throw new ArgumentException(
                $"'{key}' is not a power setting key. Expected power://<scheme>/<subgroup>/<setting>#ac.",
                nameof(key));
        }

        return (scheme, subgroup, setting);
    }
}
