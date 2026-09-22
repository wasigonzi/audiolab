using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Processes;

namespace Velocity.Tweaks.Cpu;

/// <summary>
/// Keeps a game on the cores the topology says are best, and pushes background work off them.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> Two separate things. The game is given a <em>CPU set</em> through
/// <c>SetProcessDefaultCpuSets</c>, which is a preference the scheduler may override under load.
/// Background processes are given a hard affinity mask through <c>SetProcessAffinityMask</c>, which
/// the scheduler may not override.
/// </para>
/// <para>
/// <b>Why those two are different calls.</b> A hard mask on a game is dangerous: if the chosen set
/// is wrong, or a thread pool sizes itself from the mask, the game gets slower or stalls with no
/// way for the scheduler to rescue it. A CPU set degrades gracefully. For background work the
/// opposite is true: the point is that it stays out of the way, so a hint is not enough.
/// </para>
/// <para>
/// <b>When it does anything at all.</b> Only when the topology offers a real choice:
/// <see cref="CorePlacementPolicy"/> declines on a single-complex homogeneous processor, when too
/// few cores would be left for the game, when too few would be left for background work, and when
/// the chosen cores span more than one processor group. On a plain 8 core desktop this module
/// correctly does nothing and says so.
/// </para>
/// <para>
/// <b>Rollback.</b> Background affinity is captured as ordinary state and restored generically.
/// A CPU set has no captured "previous value" to write back — the default is an empty set — so the
/// module implements <see cref="ICustomRollback"/> and clears it explicitly, using a payload that
/// is persisted with the transaction so a crash mid-session is still recoverable.
/// </para>
/// </remarks>
public sealed class GameCorePlacementTweak : ITweak, ICustomRollback
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "cpu.game-core-placement";

    /// <summary>Option naming the game executable.</summary>
    public const string GameExecutableOption = "game-executable";

    /// <summary>
    /// Option selecting how aggressive placement is: <c>IsolateGame</c> or
    /// <c>ConfineBackgroundOnly</c>.
    /// </summary>
    public const string IntentOption = "placement-intent";

    private readonly IProcessInspector _inspector;
    private readonly IProcessController _controller;

    /// <summary>Creates the module.</summary>
    /// <param name="inspector">Reads the process list.</param>
    /// <param name="controller">Applies CPU sets.</param>
    public GameCorePlacementTweak(IProcessInspector inspector, IProcessController controller)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Game core placement",
        Category = TweakCategory.Cpu,
        Summary =
            "Keeps the game on the processor's best cores and moves background work off them, on " +
            "processors where that distinction exists.",
        TechnicalDescription =
            "Gives the game process a CPU set (SetProcessDefaultCpuSets) covering the preferred " +
            "cores, and gives unprotected background processes a hard affinity mask " +
            "(SetProcessAffinityMask) covering the rest. Preferred cores come from the topology " +
            "analyzer: the performance cores on a hybrid processor, or the core complex with the " +
            "largest last level cache on a multi-chiplet processor. The module declines when the " +
            "topology offers no such distinction, when fewer than six logical processors would be " +
            "left for the game, when fewer than four would be left for background work, or when the " +
            "chosen cores span more than one processor group.",
        ExpectedEffect =
            "On a multi-chiplet processor with asymmetric cache, or a hybrid processor, keeping a " +
            "game's threads on one set of cores can reduce cross-complex migration and improve " +
            "frame time consistency. On a single-complex processor it does nothing, and the module " +
            "reports that rather than pretending otherwise. Measure it.",
        Risk = RiskLevel.Moderate,
        Scope = TweakScope.Session,
        RequiresElevation = false,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        Hardware = new HardwareRequirements { MinimumPhysicalCores = 8 },
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PlacementPlan plan = CorePlacementPolicy.Create(context.CpuLayout, ResolveIntent(context));

        if (!plan.ConstrainBackground)
        {
            return Array.Empty<StateKey>();
        }

        IReadOnlyList<ProcessSnapshot> background =
            await FindBackgroundAsync(context, cancellationToken).ConfigureAwait(false);

        return background
            .Select(process => process.ExecutableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new StateKey(ProcessStateProvider.Scheme, name, ProcessStateProvider.AffinityItem))
            .ToList();
    }

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PlacementPlan plan = CorePlacementPolicy.Create(context.CpuLayout, ResolveIntent(context));

        if (plan.IsNoOp)
        {
            return Task.FromResult(CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                plan.Notes.Count > 0
                    ? plan.Notes[0]
                    : "This processor has no group of cores worth reserving for a game."));
        }

        return Task.FromResult(CompatibilityResult.Supported(string.Join(" ", plan.Notes)));
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PlacementPlan plan = CorePlacementPolicy.Create(context.CpuLayout, ResolveIntent(context));
        ProcessSnapshot? game = await FindGameAsync(context, cancellationToken).ConfigureAwait(false);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game_processors"] = Describe(plan.GameProcessors),
            ["background_processors"] = Describe(plan.BackgroundProcessors),
            ["background_affinity_mask"] = "0x" + plan.BackgroundAffinityMask.ToString("X", CultureInfo.InvariantCulture),
            ["processor_group"] = plan.GroupId.ToString(CultureInfo.InvariantCulture),
            ["notes"] = string.Join(" ", plan.Notes),
        };

        return new TweakObservation
        {
            State = AppliedState.NotApplied,
            CurrentValueSummary = game is null
                ? "No game process is running, so placement has nothing to act on."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{game.ExecutableName} is running without a CPU set"),
            RecommendedValueSummary = plan.IsNoOp
                ? "No change: this processor offers no better core set."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Give the game {plan.GameProcessors.Count} logical processors and confine background work to {plan.BackgroundProcessors.Count}"),
            Details = details,
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PlacementPlan plan = CorePlacementPolicy.Create(context.CpuLayout, ResolveIntent(context));

        if (plan.IsNoOp)
        {
            return ApplyResult.NoChange("This processor offers no better core set for a game.");
        }

        var changed = new List<StateKey>();
        var placedGameProcessIds = new List<int>();

        if (plan.ConstrainGame)
        {
            ProcessSnapshot? game = await FindGameAsync(context, cancellationToken).ConfigureAwait(false);

            if (game is null)
            {
                return ApplyResult.NoChange(
                    "No game process is running, so there is nothing to place. Placement is applied " +
                    "when a session starts.");
            }

            if (await _controller
                    .SetCpuSetsAsync(game.ProcessId, plan.GameProcessors, cancellationToken)
                    .ConfigureAwait(false))
            {
                placedGameProcessIds.Add(game.ProcessId);
            }
        }

        if (plan.ConstrainBackground)
        {
            IReadOnlyList<ProcessSnapshot> background =
                await FindBackgroundAsync(context, cancellationToken).ConfigureAwait(false);

            foreach (IGrouping<string, ProcessSnapshot> group in background.GroupBy(
                         process => process.ExecutableName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = new StateKey(
                    ProcessStateProvider.Scheme, group.Key, ProcessStateProvider.AffinityItem);

                var values = group.ToDictionary(
                    process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
                    _ => plan.BackgroundAffinityMask.ToString(CultureInfo.InvariantCulture));

                await context.State
                    .WriteAsync(
                        key,
                        new StateValue(StateValueKind.Json, JsonSerializer.Serialize(values)),
                        cancellationToken)
                    .ConfigureAwait(false);

                changed.Add(key);
            }
        }

        // Persisted with the transaction so a crash mid-session still leaves enough to clear the
        // CPU sets on the next launch.
        context.SetRollbackPayload(JsonSerializer.Serialize(new PlacementRollback(placedGameProcessIds)));

        if (placedGameProcessIds.Count == 0 && changed.Count == 0)
        {
            return ApplyResult.NoChange("Nothing was running that placement could act on.");
        }

        return new ApplyResult
        {
            Outcome = ApplyOutcome.Applied,
            Message = string.Create(
                CultureInfo.InvariantCulture,
                $"Placed {placedGameProcessIds.Count} game process(es) and confined {changed.Count} background executable(s)."),
            ChangedKeys = changed,
        };
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PlacementPlan plan = CorePlacementPolicy.Create(context.CpuLayout, ResolveIntent(context));

        if (!plan.ConstrainBackground)
        {
            // A CPU set is a hint with no documented read-back, so there is nothing to confirm
            // beyond the call having succeeded. Saying so is better than inventing a check.
            return new VerificationResult(
                VerificationStatus.NotVerifiable,
                "Windows exposes no documented way to read a process's CPU set back, so the placement " +
                "is reported as requested rather than as confirmed.");
        }

        IReadOnlyList<ProcessSnapshot> background =
            await FindBackgroundAsync(context, cancellationToken).ConfigureAwait(false);

        List<ProcessSnapshot> unconfined = background
            .Where(process => process.AffinityMask is not null &&
                              process.AffinityMask != plan.BackgroundAffinityMask)
            .ToList();

        return unconfined.Count == 0
            ? VerificationResult.Verified("Background processes report the expected affinity mask.")
            : new VerificationResult(
                VerificationStatus.Verified,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{unconfined.Count} process(es) kept their own affinity, which usually means they set it themselves."));
    }

    /// <inheritdoc />
    public async Task RollbackAsync(
        TweakContext context,
        string? rollbackPayload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rollbackPayload))
        {
            return;
        }

        PlacementRollback? payload = JsonSerializer.Deserialize<PlacementRollback>(rollbackPayload);

        foreach (int processId in payload?.GameProcessIds ?? Array.Empty<int>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Safe to call for a process that has exited: the controller reports false and moves on.
            await _controller.ClearCpuSetsAsync(processId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessSnapshot?> FindGameAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        string game = context.GetOption(GameExecutableOption, string.Empty);
        IReadOnlyList<ProcessSnapshot> processes =
            await _inspector.GetProcessesAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(game)
            ? processes.FirstOrDefault(process => process.IsForeground)
            : processes.FirstOrDefault(process => string.Equals(
                process.ExecutableName, game, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ProcessSnapshot>> FindBackgroundAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        string game = context.GetOption(GameExecutableOption, string.Empty);
        IReadOnlyList<ProcessSnapshot> processes =
            await _inspector.GetProcessesAsync(cancellationToken).ConfigureAwait(false);

        return processes
            .Select(ProcessProtectionClassifier.Classify)
            .Where(process => process.Protection != ProcessProtection.Protected)
            .Where(process => !process.IsForeground)
            .Where(process => string.IsNullOrEmpty(game) ||
                              !string.Equals(process.ExecutableName, game, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static PlacementIntent ResolveIntent(TweakContext context) =>
        Enum.TryParse(
            context.GetOption(IntentOption, nameof(PlacementIntent.IsolateGame)),
            ignoreCase: true,
            out PlacementIntent intent)
            ? intent
            : PlacementIntent.IsolateGame;

    private static string Describe(IReadOnlyList<int> processors) =>
        processors.Count == 0
            ? "(none)"
            : string.Join(", ", processors);

    /// <summary>What rollback needs in order to clear the CPU sets it applied.</summary>
    /// <param name="GameProcessIds">Processes that were given a CPU set.</param>
    private sealed record PlacementRollback(IReadOnlyList<int> GameProcessIds);
}
