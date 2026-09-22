using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Tweaks.Network;

/// <summary>
/// Base for modules that change one driver keyword on the adapter carrying the default route.
/// </summary>
/// <remarks>
/// <para>
/// The rule that makes these modules honest is implemented once, here: <b>a keyword the driver does
/// not publish is not a setting</b>. If the value is absent from the adapter's class key, the
/// module reports itself unsupported rather than creating the value. Writing a keyword a driver
/// never reads changes nothing while appearing to succeed, and that is the failure mode this
/// product exists to avoid.
/// </para>
/// <para>
/// Only the adapter carrying the default route is touched, because that is the one carrying the
/// game's traffic, and changing a keyword on an idle adapter is noise.
/// </para>
/// <para>
/// Every one of these needs the adapter to be restarted before it takes effect, which Windows does
/// when the value changes. The modules report a restart of the adapter as expected behaviour, and
/// the momentary loss of link that comes with it.
/// </para>
/// </remarks>
public abstract class NetworkKeywordTweak : ITweak
{
    /// <summary>Creates the module.</summary>
    /// <param name="keyword">Driver keyword this module writes, for example <c>*EEE</c>.</param>
    /// <param name="enabledValue">Value meaning the feature is on.</param>
    /// <param name="disabledValue">Value meaning the feature is off.</param>
    /// <param name="turnOff">Whether the module's goal is to turn the feature off.</param>
    protected NetworkKeywordTweak(string keyword, string enabledValue, string disabledValue, bool turnOff)
    {
        Keyword = keyword ?? throw new ArgumentNullException(nameof(keyword));
        EnabledValue = enabledValue;
        DisabledValue = disabledValue;
        TargetValue = turnOff ? disabledValue : enabledValue;
    }

    /// <inheritdoc />
    public abstract TweakDescriptor Descriptor { get; }

    /// <summary>Driver keyword this module writes.</summary>
    protected string Keyword { get; }

    /// <summary>Value meaning the feature is on.</summary>
    protected string EnabledValue { get; }

    /// <summary>Value meaning the feature is off.</summary>
    protected string DisabledValue { get; }

    /// <summary>Value this module writes.</summary>
    protected string TargetValue { get; }

    /// <inheritdoc />
    public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateKey? key = ResolveKey(context);

        return Task.FromResult<IReadOnlyList<StateKey>>(
            key is null ? Array.Empty<StateKey>() : new[] { key.Value });
    }

    /// <inheritdoc />
    public async Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        NetworkAdapter? adapter = FindPrimaryAdapter(context);

        if (adapter is null)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                "No active adapter is carrying the default route.");
        }

        if (adapter.DriverRegistryPath is null)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.Unknown,
                "The adapter's driver settings could not be located in the registry.");
        }

        if (!adapter.Capabilities.AdvancedProperties.ContainsKey(Keyword))
        {
            // The honest answer, and the one most optimizers get wrong.
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                $"The driver for {adapter.Description} does not publish {Keyword}, so this setting " +
                "does not exist on this adapter. Writing it would change nothing.");
        }

        StateKey key = ResolveKey(context)!.Value;
        StateValue current = await context.State.ReadAsync(key, cancellationToken).ConfigureAwait(false);

        return string.Equals(current.Data, TargetValue, StringComparison.OrdinalIgnoreCase)
            ? CompatibilityResult.Unsupported(
                CompatibilityStatus.AlreadyOptimal, "The adapter is already configured this way.")
            : CompatibilityResult.Supported($"{adapter.Description} publishes {Keyword}.");
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        NetworkAdapter? adapter = FindPrimaryAdapter(context);
        StateKey? key = ResolveKey(context);

        if (adapter is null || key is null)
        {
            return new TweakObservation
            {
                State = AppliedState.Unknown,
                CurrentValueSummary = "No adapter is carrying the default route.",
                RecommendedValueSummary = "No change",
            };
        }

        StateValue current = await context.State.ReadAsync(key.Value, cancellationToken).ConfigureAwait(false);
        bool applied = string.Equals(current.Data, TargetValue, StringComparison.OrdinalIgnoreCase);

        return new TweakObservation
        {
            State = applied ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = current.IsAbsent
                ? $"{Keyword} is not published by this driver"
                : $"{Keyword} = {current.Data} on {adapter.Name}",
            RecommendedValueSummary = $"{Keyword} = {TargetValue}",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["adapter"] = adapter.Description,
                ["registry_key"] = key.Value.ToString(),
                ["link_speed_mbps"] = (adapter.LinkSpeedBitsPerSecond / 1_000_000)
                    .ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateKey? key = ResolveKey(context);

        if (key is null)
        {
            return ApplyResult.NoChange("No adapter is carrying the default route.");
        }

        StateValue current = await context.State.ReadAsync(key.Value, cancellationToken).ConfigureAwait(false);

        if (current.IsAbsent)
        {
            // Creating a keyword the driver never reads would look like success and do nothing.
            return ApplyResult.NoChange(
                $"The driver does not publish {Keyword}, so there is nothing to change.");
        }

        if (string.Equals(current.Data, TargetValue, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyResult.NoChange($"{Keyword} is already {TargetValue}.");
        }

        await context.State
            .WriteAsync(key.Value, StateValue.FromString(TargetValue), cancellationToken)
            .ConfigureAwait(false);

        return new ApplyResult
        {
            Outcome = ApplyOutcome.Applied,
            Message = $"{Keyword} set to {TargetValue}. Windows restarts the adapter to apply it, " +
                      "so the link drops for a moment.",
            ChangedKeys = new[] { key.Value },
        };
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateKey? key = ResolveKey(context);

        if (key is null)
        {
            return VerificationResult.Mismatch("The adapter is no longer present.");
        }

        StateValue current = await context.State.ReadAsync(key.Value, cancellationToken).ConfigureAwait(false);

        return string.Equals(current.Data, TargetValue, StringComparison.OrdinalIgnoreCase)
            ? VerificationResult.Verified($"{Keyword} reads back as {TargetValue}.")
            : VerificationResult.Mismatch(
                $"Expected {Keyword} = {TargetValue} but the driver reports {current.ToDisplayString()}.");
    }

    /// <summary>Finds the adapter carrying the default route.</summary>
    /// <param name="context">Execution context.</param>
    /// <returns>The adapter, or <see langword="null"/> when none qualifies.</returns>
    protected static NetworkAdapter? FindPrimaryAdapter(TweakContext context) =>
        context.Profile.NetworkAdapters
            .Where(adapter => adapter.IsUp && adapter.CarriesDefaultRoute)
            .Where(adapter => adapter.Kind is NetworkInterfaceKind.Ethernet or NetworkInterfaceKind.Wireless)
            .OrderByDescending(adapter => adapter.LinkSpeedBitsPerSecond)
            .FirstOrDefault();

    private StateKey? ResolveKey(TweakContext context)
    {
        NetworkAdapter? adapter = FindPrimaryAdapter(context);

        return adapter?.DriverRegistryPath is null
            ? null
            : RegistryKeys.Value(adapter.DriverRegistryPath, Keyword);
    }
}
