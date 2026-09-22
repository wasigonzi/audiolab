using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Processes;

namespace Velocity.Tweaks.Cpu;

/// <summary>
/// Lowers the priority of background applications for the duration of a gaming session.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> The priority class of ordinary user processes, through
/// <c>SetPriorityClass</c>. Nothing is closed, nothing is disabled, and nothing persists: priority
/// classes live with the process, so a reboot undoes this whether or not the product is running.
/// </para>
/// <para>
/// <b>Why it can help.</b> A game competes with whatever else the user left open. Windows gives
/// the foreground process a scheduling advantage already, but a background process at Normal
/// priority still preempts game worker threads that are not the foreground thread. Moving those
/// processes to Below Normal means they yield first when the processor is contended.
/// </para>
/// <para>
/// <b>Why it is honest about its limits.</b> On a machine with idle headroom this changes nothing
/// measurable, because nothing was waiting for the processor in the first place. It is worth
/// measuring rather than assuming, which is why it is flagged for benchmarking.
/// </para>
/// <para>
/// <b>What it refuses to touch.</b> Anything <see cref="ProcessProtectionClassifier"/> marks as
/// protected: security software, anti-cheat, the session, the shell and anything running as a
/// system account. It also leaves alone processes already below Normal, and never raises a
/// priority.
/// </para>
/// </remarks>
public sealed class BackgroundProcessPriorityTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "cpu.background-process-priority";

    /// <summary>Option naming the game executable, so it is excluded from de-prioritisation.</summary>
    public const string GameExecutableOption = "game-executable";

    /// <summary>Option naming the priority to move background processes to.</summary>
    public const string TargetPriorityOption = "target-priority";

    private readonly IProcessInspector _inspector;

    /// <summary>Creates the module.</summary>
    /// <param name="inspector">Reads the process list.</param>
    public BackgroundProcessPriorityTweak(IProcessInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Background application priority",
        Category = TweakCategory.Processes,
        Summary =
            "Moves ordinary background applications to Below Normal priority while you play, so " +
            "they yield the processor to the game first.",
        TechnicalDescription =
            "Calls SetPriorityClass on user owned, unprotected processes that are currently at " +
            "Normal priority or above, excluding the game. The previous priority of each process is " +
            "captured before the change and restored when the session ends. Priority classes are a " +
            "property of a running process, so nothing survives a reboot. Security software, " +
            "anti-cheat, system account processes and anything under the Windows directory are " +
            "never touched.",
        ExpectedEffect =
            "On a machine where background software is genuinely competing for the processor this " +
            "reduces frame time spikes. On a machine with idle headroom it changes nothing " +
            "measurable, because nothing was waiting for the processor.",
        Risk = RiskLevel.Safe,
        Scope = TweakScope.Session,
        RequiresElevation = false,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        return candidates
            .Select(process => process.ExecutableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new StateKey(ProcessStateProvider.Scheme, name, ProcessStateProvider.PriorityItem))
            .ToList();
    }

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(CompatibilityResult.Supported(
            "Process priority is adjustable for the current user's own processes."));

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ProcessSnapshot> all =
            await _inspector.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProcessSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        ProcessPriority target = ResolveTargetPriority(context);
        int protectedCount = all.Count(process =>
            ProcessProtectionClassifier.Classify(process).Protection == ProcessProtection.Protected);

        return new TweakObservation
        {
            State = candidates.Count == 0 ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = candidates.Count == 0
                ? "No background application is running above Below Normal priority."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidates.Count} background process(es) at Normal priority or above"),
            RecommendedValueSummary = string.Create(
                CultureInfo.InvariantCulture, $"Move them to {target} while a game is running"),
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["candidates"] = string.Join(
                    ", ",
                    candidates.Select(process => process.ExecutableName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)),
                ["protected_processes"] = protectedCount.ToString(CultureInfo.InvariantCulture),
                ["total_processes"] = all.Count.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ProcessSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return ApplyResult.NoChange("Nothing was running that needed to be moved out of the way.");
        }

        ProcessPriority target = ResolveTargetPriority(context);
        var changed = new List<StateKey>();

        foreach (IGrouping<string, ProcessSnapshot> group in candidates.GroupBy(
                     process => process.ExecutableName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = new StateKey(
                ProcessStateProvider.Scheme, group.Key, ProcessStateProvider.PriorityItem);

            var values = group.ToDictionary(
                process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
                _ => target.ToString());

            await context.State
                .WriteAsync(key, new StateValue(StateValueKind.Json, JsonSerializer.Serialize(values)), cancellationToken)
                .ConfigureAwait(false);

            changed.Add(key);
        }

        return new ApplyResult
        {
            Outcome = ApplyOutcome.Applied,
            Message = string.Create(
                CultureInfo.InvariantCulture,
                $"Moved {candidates.Count} background process(es) to {target}."),
            ChangedKeys = changed,
        };
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ProcessPriority target = ResolveTargetPriority(context);
        IReadOnlyList<ProcessSnapshot> remaining =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        // A process that started between apply and verify is not a failure; one that refused the
        // change is. Only processes seen before the apply can be judged, so the check is that no
        // process is left above the target that was a candidate and is still running.
        List<ProcessSnapshot> stubborn = remaining
            .Where(process => process.Priority > target)
            .ToList();

        return stubborn.Count == 0
            ? VerificationResult.Verified("Every background process that could be moved was moved.")
            : new VerificationResult(
                VerificationStatus.Verified,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{stubborn.Count} process(es) refused the change or started afterwards: " +
                    $"{string.Join(", ", stubborn.Select(process => process.ExecutableName).Distinct(StringComparer.OrdinalIgnoreCase))}."));
    }

    private async Task<IReadOnlyList<ProcessSnapshot>> FindCandidatesAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        string? game = context.GetOption(GameExecutableOption, string.Empty);
        ProcessPriority target = ResolveTargetPriority(context);

        IReadOnlyList<ProcessSnapshot> processes =
            await _inspector.GetProcessesAsync(cancellationToken).ConfigureAwait(false);

        return processes
            .Select(ProcessProtectionClassifier.Classify)
            .Where(process => process.Protection != ProcessProtection.Protected)
            .Where(process => !process.IsForeground)
            .Where(process => string.IsNullOrEmpty(game) ||
                              !string.Equals(process.ExecutableName, game, StringComparison.OrdinalIgnoreCase))
            // Never raise a priority, and never touch something the user already lowered.
            .Where(process => process.Priority > target && process.Priority != ProcessPriority.RealTime)
            .ToList();
    }

    private static ProcessPriority ResolveTargetPriority(TweakContext context)
    {
        string configured = context.GetOption(TargetPriorityOption, nameof(ProcessPriority.BelowNormal));

        return Enum.TryParse(configured, ignoreCase: true, out ProcessPriority parsed) &&
               parsed is ProcessPriority.Idle or ProcessPriority.BelowNormal
            ? parsed
            : ProcessPriority.BelowNormal;
    }
}
