using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Tweaks.Gpu;

/// <summary>
/// Turns Hardware Accelerated GPU Scheduling on.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> <c>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers!HwSchMode</c>.
/// 2 is enabled, 1 is disabled. Windows reads it when the graphics stack initialises, so a restart
/// is required.
/// </para>
/// <para>
/// <b>What it actually does.</b> Moves scheduling of GPU work from an operating system thread to
/// the GPU's own scheduler. That removes one layer of buffering between the game and the hardware.
/// </para>
/// <para>
/// <b>Why the direction is not obvious.</b> Published measurements are genuinely mixed: some titles
/// gain a little, some lose a little, and on some driver versions it has caused instability. This
/// module therefore presents it as a change to measure, not an improvement to apply. It is the
/// clearest case in the product for the auto-tune loop.
/// </para>
/// <para>
/// <b>Detection limit.</b> Windows exposes no documented way to ask whether an adapter and driver
/// support hardware scheduling. The absence of the value means the platform never offered it, which
/// is reported as unknown rather than as "off".
/// </para>
/// </remarks>
public sealed class HardwareSchedulingTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "gpu.hardware-scheduling";

    /// <summary>Option choosing whether to enable or disable hardware scheduling.</summary>
    public const string EnabledOption = "enabled";

    private const uint Enabled = 2;
    private const uint Disabled = 1;

    private static readonly StateKey Key =
        RegistryKeys.Value(RegistryKeys.GraphicsDrivers, "HwSchMode");

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Hardware accelerated GPU scheduling",
        Category = TweakCategory.Gpu,
        Summary =
            "Lets the graphics card schedule its own work instead of Windows doing it, removing a " +
            "layer of buffering. Needs a restart.",
        TechnicalDescription =
            @"Writes HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers!HwSchMode (REG_DWORD): " +
            "2 enables hardware scheduling, 1 disables it. The graphics stack reads the value when " +
            "it initialises, so the change takes effect after a restart. Windows exposes no " +
            "documented way to query whether the adapter and driver support the feature, so an " +
            "absent value is reported as unknown rather than as disabled.",
        ExpectedEffect =
            "Measurements published for this setting are genuinely mixed: some titles gain a " +
            "little, some lose a little, and some driver versions have been unstable with it. " +
            "Treat it as a change to measure on your machine, not as an improvement.",
        Risk = RiskLevel.Moderate,
        Scope = TweakScope.Persistent,
        RequiresElevation = true,
        RequiresRestart = true,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        Hardware = new HardwareRequirements
        {
            GpuVendors = new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel },
        },
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StateKey>>(new[] { Key });

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        GpuDevice? adapter = context.Profile.Gpus
            .FirstOrDefault(gpu => gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel);

        if (adapter is null)
        {
            return Task.FromResult(CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                "No supported display adapter was detected."));
        }

        return Task.FromResult(adapter.HardwareScheduling == HardwareSchedulingState.NotSupported
            ? CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                $"{adapter.Description} reports that it does not support hardware scheduling.")
            : CompatibilityResult.Supported($"{adapter.Description} is present."));
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);
        uint target = ResolveTarget(context);

        return new TweakObservation
        {
            State = current.AsUInt32() == target ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = Describe(current),
            RecommendedValueSummary = target == Enabled ? "Enabled" : "Disabled",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HwSchMode"] = current.ToDisplayString(),
                ["adapter"] = context.Profile.Gpus.Count > 0
                    ? context.Profile.Gpus[0].Description
                    : "(none)",
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        uint target = ResolveTarget(context);
        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);

        if (current.AsUInt32() == target)
        {
            return ApplyResult.NoChange($"Hardware scheduling is already {(target == Enabled ? "on" : "off")}.");
        }

        await context.State
            .WriteAsync(Key, StateValue.FromUInt32(target), cancellationToken)
            .ConfigureAwait(false);

        return new ApplyResult
        {
            Outcome = ApplyOutcome.AppliedPendingRestart,
            Message = $"Hardware scheduling set to {(target == Enabled ? "on" : "off")}. Restart to apply it.",
            ChangedKeys = new[] { Key },
        };
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        uint target = ResolveTarget(context);
        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);

        return current.AsUInt32() == target
            ? new VerificationResult(
                VerificationStatus.PendingRestart,
                "The value was written. The graphics stack adopts it at the next restart; this " +
                "module does not claim it is in effect before then.")
            : VerificationResult.Mismatch(
                $"Expected HwSchMode = {target} but the machine reports {current.ToDisplayString()}.");
    }

    private static uint ResolveTarget(TweakContext context) =>
        context.GetOption(EnabledOption, true) ? Enabled : Disabled;

    private static string Describe(StateValue value) => value.AsUInt32() switch
    {
        Enabled => "Enabled",
        Disabled => "Disabled",
        null => "Not set, so this platform never offered it",
        _ => value.ToDisplayString(),
    };
}
