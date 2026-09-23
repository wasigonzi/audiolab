using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Power;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Power;

namespace Velocity.Tweaks.Power;

/// <summary>
/// Switches to a power plan that does not clock the processor down between frames.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> The active Windows power scheme, through
/// <c>PowerSetActiveScheme</c>. The previous scheme's GUID is captured first, so restoring is
/// exact rather than "set it back to Balanced".
/// </para>
/// <para>
/// <b>Why it can matter.</b> Under Balanced, the processor drops to a low performance state when it
/// looks idle. A game that alternates between a busy render thread and brief waits can be caught by
/// that, and the ramp back up shows as a frame time spike rather than as lower average frame rate.
/// </para>
/// <para>
/// <b>Why it is honest about its limits.</b> Modern processors with hardware managed performance
/// states make their own decisions and largely ignore the operating system's hints, so on a recent
/// machine this often changes nothing measurable. It is also a battery life decision, which is why
/// the module refuses to run on a laptop that is not plugged in.
/// </para>
/// <para>
/// <b>What it never does.</b> It does not create a custom plan, does not disable core parking, and
/// does not touch the DC side. Making a machine behave differently on battery without being asked
/// is not this product's call.
/// </para>
/// </remarks>
public sealed class PowerPlanTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "power.performance-plan";

    /// <summary>Option naming the scheme GUID to activate.</summary>
    public const string SchemeOption = "scheme-guid";

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Performance power plan",
        Category = TweakCategory.Power,
        Summary =
            "Switches to a power plan that keeps the processor from clocking down between frames. " +
            "Your previous plan is restored afterwards.",
        TechnicalDescription =
            "Calls PowerSetActiveScheme with the High performance scheme GUID " +
            "(8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c), or with a GUID supplied through the " +
            "scheme-guid option. The GUID of the previously active scheme is captured before the " +
            "change, so the restore returns the exact plan the user had rather than assuming " +
            "Balanced. Only the active scheme is changed; no setting inside any plan is modified " +
            "and the DC side is untouched.",
        ExpectedEffect =
            "On processors where Windows still drives the performance state, this removes ramp-up " +
            "spikes after brief idle moments. On recent processors with hardware managed states it " +
            "usually changes nothing measurable. It also increases idle power draw and heat.",
        Risk = RiskLevel.Low,
        Scope = TweakScope.Session,
        RequiresElevation = true,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StateKey>>(new[] { PowerStateProvider.ActiveSchemeKey() });

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PowerConfiguration power = context.Profile.Power;

        if (power.HasBattery && power.IsOnBattery)
        {
            return Task.FromResult(CompatibilityResult.Unsupported(
                CompatibilityStatus.Blocked,
                "This machine is running on battery. Changing the power plan now would cost battery " +
                "life without being asked; plug in first."));
        }

        Guid target = ResolveTarget(context);

        if (power.AvailableSchemes.Count > 0 &&
            !power.AvailableSchemes.Any(scheme => scheme.SchemeGuid == target))
        {
            return Task.FromResult(CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedOperatingSystem,
                "The requested power plan does not exist on this edition of Windows."));
        }

        return Task.FromResult(power.ActiveScheme.SchemeGuid == target
            ? CompatibilityResult.Unsupported(
                CompatibilityStatus.AlreadyOptimal, $"{power.ActiveScheme.Name} is already active.")
            : CompatibilityResult.Supported("The requested power plan is available."));
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateValue current = await context.State
            .ReadAsync(PowerStateProvider.ActiveSchemeKey(), cancellationToken)
            .ConfigureAwait(false);

        Guid target = ResolveTarget(context);
        bool applied = Guid.TryParse(current.Data, out Guid active) && active == target;

        return new TweakObservation
        {
            State = applied ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = context.Profile.Power.ActiveScheme.Name,
            RecommendedValueSummary = DescribeScheme(target),
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["active_scheme_guid"] = current.ToDisplayString(),
                ["target_scheme_guid"] = target.ToString("D", CultureInfo.InvariantCulture),
                ["on_battery"] = context.Profile.Power.IsOnBattery
                    .ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateKey key = PowerStateProvider.ActiveSchemeKey();
        Guid target = ResolveTarget(context);

        StateValue current = await context.State.ReadAsync(key, cancellationToken).ConfigureAwait(false);

        if (Guid.TryParse(current.Data, out Guid active) && active == target)
        {
            return ApplyResult.NoChange($"{DescribeScheme(target)} is already active.");
        }

        await context.State
            .WriteAsync(key, StateValue.FromString(target.ToString("D", CultureInfo.InvariantCulture)), cancellationToken)
            .ConfigureAwait(false);

        return ApplyResult.Applied($"Switched to {DescribeScheme(target)}.", key);
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateValue current = await context.State
            .ReadAsync(PowerStateProvider.ActiveSchemeKey(), cancellationToken)
            .ConfigureAwait(false);

        Guid target = ResolveTarget(context);

        return Guid.TryParse(current.Data, out Guid active) && active == target
            ? VerificationResult.Verified($"{DescribeScheme(target)} is active.")
            : VerificationResult.Mismatch(
                $"Expected {DescribeScheme(target)} but the machine reports {current.ToDisplayString()}.");
    }

    private static Guid ResolveTarget(TweakContext context) =>
        Guid.TryParse(context.GetOption(SchemeOption, string.Empty), out Guid configured)
            ? configured
            : PowerSettings.HighPerformanceScheme;

    private static string DescribeScheme(Guid scheme) =>
        scheme == PowerSettings.HighPerformanceScheme ? "High performance"
        : scheme == PowerSettings.UltimatePerformanceScheme ? "Ultimate performance"
        : scheme == PowerSettings.BalancedScheme ? "Balanced"
        : scheme.ToString("D", CultureInfo.InvariantCulture);
}
