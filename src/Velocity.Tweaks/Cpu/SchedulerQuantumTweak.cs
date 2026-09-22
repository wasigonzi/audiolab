using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Tweaks.Cpu;

/// <summary>
/// Configures the kernel's thread scheduling quantums through <c>Win32PrioritySeparation</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> The Windows thread scheduler. <c>PsPrioritySeparation</c>, which decides
/// quantum length, whether the foreground process gets a stretched quantum, and by how much, is
/// derived from
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl!Win32PrioritySeparation</c>.
/// </para>
/// <para>
/// <b>What Windows does by default.</b> A client installation uses decimal 2: short, variable
/// quantums with the maximum (3x) foreground boost. Short quantums and maximum foreground boost are
/// therefore <em>already</em> in effect on a normal gaming PC, which is why the many guides
/// recommending this tweak "for more FPS" are mostly recommending the default back to itself.
/// </para>
/// <para>
/// <b>What this module actually changes.</b> One thing: variable quantums become fixed, so a
/// background thread's timeslice is no longer cut short relative to the foreground process's. For
/// a game whose worker threads sit outside the foreground window's thread, that can reduce
/// scheduling jitter. For a game that does not work that way, it changes nothing measurable.
/// </para>
/// <para>
/// <b>Why it is honest to ship it anyway.</b> It is documented, it is a single reversible DWORD,
/// its effect is measurable with frame time capture, and the product's job is to find out whether
/// it helps <em>this</em> machine rather than to assert that it does. It is flagged
/// <see cref="TweakDescriptor.BenchmarkRecommended"/> for exactly that reason.
/// </para>
/// <para>
/// <b>Verification limit.</b> Windows reads this value when the system starts. This module verifies
/// that the value was written, and reports the result as pending a restart rather than claiming the
/// kernel has adopted it, because it cannot observe <c>PsPrioritySeparation</c> from user mode.
/// </para>
/// </remarks>
public sealed class SchedulerQuantumTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "cpu.scheduler-quantum";

    /// <summary>
    /// Option name for the raw value to write. Lets a profile or an auto-tune trial explore other
    /// encodings without a code change.
    /// </summary>
    public const string RawValueOption = "raw-value";

    private static readonly StateKey Key =
        RegistryKeys.Value(RegistryKeys.PriorityControl, "Win32PrioritySeparation");

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Scheduler quantum configuration",
        Category = TweakCategory.Scheduler,
        Summary =
            "Switches Windows from variable to fixed thread timeslices, so background threads are " +
            "not cut short relative to the game window.",
        TechnicalDescription =
            @"Writes HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl!Win32PrioritySeparation " +
            "(REG_DWORD). The value is six bits: bits 0-1 foreground boost (0 none, 1 double, " +
            "2 triple), bits 2-3 quantum type (0 OS default, 1 variable, 2 fixed), bits 4-5 quantum " +
            "length (0 OS default, 1 short, 2 long). Windows client defaults to decimal 2, which is " +
            "short variable quantums with the 3x foreground boost already enabled. This module " +
            "writes decimal 26 by default: short, FIXED quantums, 3x boost. The kernel reads the " +
            "value at boot, so the change is verified as written rather than as in effect.",
        ExpectedEffect =
            "Short quantums and the maximum foreground boost are already the Windows default, so " +
            "the only real change is variable to fixed timeslices. On some games this reduces " +
            "frame time jitter; on many it changes nothing measurable. Benchmark it rather than " +
            "assuming it helps.",
        Risk = RiskLevel.Low,
        Scope = TweakScope.Persistent,
        RequiresElevation = true,
        RequiresRestart = true,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        Hardware = HardwareRequirements.None,
        DefinitionVersion = 1,
        Documentation = new Uri(
            "https://learn.microsoft.com/windows-server/administration/performance-tuning/"),
    };

    /// <inheritdoc />
    public IReadOnlyList<StateKey> GetStateKeys(TweakContext context) => new[] { Key };

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Quantum tuning is a desktop concern. On a laptop the scheduler is not what limits frame
        // times, and on battery a fixed quantum works against the power manager.
        if (context.Profile.MachineKind == MachineKind.VirtualMachine)
        {
            return Task.FromResult(CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                "Inside a virtual machine the host scheduler decides timeslices, so this would not " +
                "be measurable."));
        }

        return Task.FromResult(CompatibilityResult.Supported(
            "Windows exposes the scheduling quantum configuration on this build."));
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);
        PrioritySeparation target = ResolveTarget(context);

        if (current.IsAbsent)
        {
            // Absent means the machine is running the edition default, which on client Windows is
            // decimal 2. That is reported as what it is, not as "unknown".
            return new TweakObservation
            {
                State = AppliedState.NotApplied,
                CurrentValueSummary =
                    $"Not set, so Windows uses its default: {PrioritySeparation.WindowsClientDefault}",
                RecommendedValueSummary = target.ToString(),
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["registry_value"] = "(not set)",
                    ["effective_default"] = PrioritySeparation.WindowsClientDefault.ToRaw()
                        .ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        uint raw = current.AsUInt32() ?? 0u;
        PrioritySeparation decoded = PrioritySeparation.FromRaw(raw);

        return new TweakObservation
        {
            State = raw == target.ToRaw() ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = decoded.ToString(),
            RecommendedValueSummary = target.ToString(),
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["registry_value"] = raw.ToString(CultureInfo.InvariantCulture),
                ["foreground_boost"] = decoded.Boost.ToString(),
                ["quantum_type"] = decoded.Type.ToString(),
                ["quantum_length"] = decoded.Length.ToString(),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PrioritySeparation target = ResolveTarget(context);
        uint targetRaw = target.ToRaw();

        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);

        if (!current.IsAbsent && current.AsUInt32() == targetRaw)
        {
            return ApplyResult.NoChange($"Already set to {target}.");
        }

        await context.State
            .WriteAsync(Key, StateValue.FromUInt32(targetRaw), cancellationToken)
            .ConfigureAwait(false);

        return new ApplyResult
        {
            Outcome = ApplyOutcome.AppliedPendingRestart,
            Message = $"Set to {target}. Windows reads this value at boot, so restart to apply it.",
            ChangedKeys = new[] { Key },
        };
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PrioritySeparation target = ResolveTarget(context);
        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);
        uint? raw = current.AsUInt32();

        if (raw != target.ToRaw())
        {
            return VerificationResult.Mismatch(
                $"Expected {target.ToRaw()} but the machine reports {current.ToDisplayString()}.");
        }

        return new VerificationResult(
            VerificationStatus.PendingRestart,
            "The value was written and read back correctly. The kernel adopts it at the next " +
            "restart; this module does not claim it is in effect before then.");
    }

    private static PrioritySeparation ResolveTarget(TweakContext context)
    {
        int configured = context.GetOption(RawValueOption, -1);

        return configured is >= 0 and <= 0b111111
            ? PrioritySeparation.FromRaw((uint)configured)
            : PrioritySeparation.ShortFixedMaximumBoost;
    }
}
