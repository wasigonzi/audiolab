using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Presentation.Mvvm;

namespace Velocity.Presentation.ViewModels;

/// <summary>
/// One optimization module as the Advanced and Expert pages show it.
/// </summary>
/// <remarks>
/// Every field the brief requires is present and comes from the descriptor or from a live read of
/// the machine: name, description, current state, recommended state, expected effect,
/// compatibility, risk, restart, benchmark recommendation, applied state. The Expert fields
/// (<see cref="StateKeys"/>, <see cref="OriginalValue"/>, <see cref="TechnicalDescription"/>) are
/// read from the same source the engine uses, so Expert Mode can never show a setting that does
/// not actually exist.
/// </remarks>
public sealed partial class TweakItemViewModel : ObservableObject
{
    /// <summary>Creates the item.</summary>
    /// <param name="descriptor">Module metadata.</param>
    public TweakItemViewModel(TweakDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    /// <summary>Module metadata.</summary>
    public TweakDescriptor Descriptor { get; }

    /// <summary>Stable identifier.</summary>
    public string Id => Descriptor.Id;

    /// <summary>Display name.</summary>
    public string Name => Descriptor.Name;

    /// <summary>One sentence explanation for a normal gamer.</summary>
    public string Summary => Descriptor.Summary;

    /// <summary>Full technical explanation, shown in Expert Mode.</summary>
    public string TechnicalDescription => Descriptor.TechnicalDescription;

    /// <summary>What the user should expect, written honestly.</summary>
    public string ExpectedEffect => Descriptor.ExpectedEffect;

    /// <summary>Risk classification.</summary>
    public RiskLevel Risk => Descriptor.Risk;

    /// <summary>Whether the change survives a reboot or is reverted with the session.</summary>
    public TweakScope Scope => Descriptor.Scope;

    /// <summary>Whether a restart is needed before the change takes effect.</summary>
    public bool RequiresRestart => Descriptor.RequiresRestart;

    /// <summary>Whether the change should be measured rather than assumed to help.</summary>
    public bool BenchmarkRecommended => Descriptor.BenchmarkRecommended;

    /// <summary>Whether administrator rights are needed.</summary>
    public bool RequiresElevation => Descriptor.RequiresElevation;

    /// <summary>Compatibility on this machine.</summary>
    [ObservableProperty]
    public partial CompatibilityStatus Compatibility { get; set; } = CompatibilityStatus.Unknown;

    /// <summary>Why the module is or is not available here.</summary>
    [ObservableProperty]
    public partial string CompatibilityReason { get; set; } = string.Empty;

    /// <summary>Whether the machine is currently in the state this module produces.</summary>
    [ObservableProperty]
    public partial AppliedState State { get; set; } = AppliedState.Unknown;

    /// <summary>The machine's current configuration, in one line.</summary>
    [ObservableProperty]
    public partial string CurrentValue { get; set; } = "—";

    /// <summary>What this module would change it to.</summary>
    [ObservableProperty]
    public partial string RecommendedValue { get; set; } = "—";

    /// <summary>Whether this product applied the change (as opposed to it already being set).</summary>
    [ObservableProperty]
    public partial bool AppliedByVelocity { get; set; }

    /// <summary>Value captured before this product changed anything, when it did.</summary>
    [ObservableProperty]
    public partial string? OriginalValue { get; set; }

    /// <summary>The exact state this module reads and writes. Expert Mode.</summary>
    public ObservableCollection<string> StateKeys { get; } = new();

    /// <summary>Raw values behind the summary. Expert Mode.</summary>
    public ObservableCollection<string> Details { get; } = new();

    /// <summary><see langword="true"/> when the module can be applied right now.</summary>
    public bool CanApply => Compatibility == CompatibilityStatus.Supported && State != AppliedState.Applied;

    /// <summary><see langword="true"/> when this product has something to undo here.</summary>
    public bool CanRestore => AppliedByVelocity;
}

/// <summary>
/// The page behind each sidebar category: every module in that category with its live state.
/// </summary>
public sealed partial class TweakCategoryViewModel : ViewModelBase
{
    private readonly ITweakRegistry _registry;
    private readonly ITweakContextFactory _contextFactory;
    private readonly IOptimizationEngine _engine;
    private readonly IRollbackEngine _rollback;
    private readonly IAppliedTweakReader _appliedTweaks;
    private readonly IApplicationModeService _modeService;

    /// <summary>Creates the page.</summary>
    /// <param name="registry">Tweak catalogue.</param>
    /// <param name="contextFactory">Execution context factory.</param>
    /// <param name="engine">Optimization engine.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="appliedTweaks">Reader for what this product has applied.</param>
    /// <param name="modeService">Easy/Advanced/Expert mode.</param>
    /// <param name="logger">Logger.</param>
    public TweakCategoryViewModel(
        ITweakRegistry registry,
        ITweakContextFactory contextFactory,
        IOptimizationEngine engine,
        IRollbackEngine rollback,
        IAppliedTweakReader appliedTweaks,
        IApplicationModeService modeService,
        ILogger<TweakCategoryViewModel> logger)
        : base(logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _appliedTweaks = appliedTweaks ?? throw new ArgumentNullException(nameof(appliedTweaks));
        _modeService = modeService ?? throw new ArgumentNullException(nameof(modeService));
    }

    /// <summary>The category this page is showing.</summary>
    [ObservableProperty]
    public partial TweakCategory Category { get; set; } = TweakCategory.Cpu;

    /// <summary>Modules in the category.</summary>
    public ObservableCollection<TweakItemViewModel> Items { get; } = new();

    /// <summary>Whether the Expert Mode fields should be shown.</summary>
    public bool ShowExpertDetail => _modeService.Mode == ApplicationMode.Expert;

    /// <summary>Message shown when the category is empty in this build.</summary>
    [ObservableProperty]
    public partial string? EmptyMessage { get; set; }

    /// <summary>Loads the modules in a category and reads their current state.</summary>
    /// <param name="category">Category to show.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    [RelayCommand]
    public Task LoadAsync(TweakCategory category) =>
        RunAsync(async token =>
        {
            Category = category;
            Items.Clear();

            IReadOnlyList<ITweak> tweaks = _registry.InCategory(category);

            if (tweaks.Count == 0)
            {
                EmptyMessage =
                    "This build contains no modules in this category yet. The engine, journal and " +
                    "rollback pipeline are in place; modules are added per phase.";
                return;
            }

            EmptyMessage = null;
            IReadOnlyDictionary<string, string?> applied =
                await _appliedTweaks.GetAppliedOriginalValuesAsync(token).ConfigureAwait(true);

            foreach (ITweak tweak in tweaks)
            {
                token.ThrowIfCancellationRequested();
                Items.Add(await BuildItemAsync(tweak, applied, token).ConfigureAwait(true));
            }
        },
        "Reading current settings");

    /// <summary>Applies one module.</summary>
    /// <param name="item">Module to apply.</param>
    /// <returns>A task that completes when the run has finished.</returns>
    [RelayCommand]
    public Task ApplyAsync(TweakItemViewModel? item) =>
        item is null
            ? Task.CompletedTask
            : RunAsync(async token =>
                {
                    OptimizationRunResult result = await _engine.ApplyAsync(
                        new OptimizationRequest
                        {
                            TweakIds = new[] { item.Id },
                            Reason = TransactionReason.ManualTweak,
                        },
                        token).ConfigureAwait(true);

                    TweakRunResult run = result.Results[0];
                    if (run.Outcome == ApplyOutcome.Failed)
                    {
                        ErrorMessage = run.Message;
                    }

                    await LoadAsync(Category).ConfigureAwait(true);
                },
                $"Applying {item.Name}");

    /// <summary>Restores one module.</summary>
    /// <param name="item">Module to restore.</param>
    /// <returns>A task that completes when the rollback has finished.</returns>
    [RelayCommand]
    public Task RestoreAsync(TweakItemViewModel? item) =>
        item is null
            ? Task.CompletedTask
            : RunAsync(async token =>
                {
                    RollbackResult? result =
                        await _rollback.RollbackTweakAsync(item.Id, token).ConfigureAwait(true);

                    if (result is { Succeeded: false })
                    {
                        ErrorMessage = string.Join("; ", result.Failures.Values);
                    }

                    await LoadAsync(Category).ConfigureAwait(true);
                },
                $"Restoring {item.Name}");

    private async Task<TweakItemViewModel> BuildItemAsync(
        ITweak tweak,
        IReadOnlyDictionary<string, string?> applied,
        CancellationToken cancellationToken)
    {
        var item = new TweakItemViewModel(tweak.Descriptor);

        (TweakContext context, _) =
            await _contextFactory.CreateAsync(tweak, null, cancellationToken).ConfigureAwait(true);

        CompatibilityResult compatibility = CompatibilityEvaluator.Evaluate(
            tweak.Descriptor, context.Profile, context.CpuLayout, context.Privileges);

        if (compatibility.IsSupported)
        {
            try
            {
                compatibility = await tweak.CheckCompatibilityAsync(context, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Compatibility check for {TweakId} threw.", tweak.Descriptor.Id);
                compatibility = CompatibilityResult.Unsupported(
                    CompatibilityStatus.Unknown, $"Compatibility could not be determined: {ex.Message}");
            }
        }

        item.Compatibility = compatibility.Status;
        item.CompatibilityReason = compatibility.Reason;

        foreach (StateKey key in await tweak.GetStateKeysAsync(context, cancellationToken).ConfigureAwait(true))
        {
            item.StateKeys.Add(key.ToString());
        }

        if (compatibility.IsSupported)
        {
            try
            {
                TweakObservation observation =
                    await tweak.DetectAsync(context, cancellationToken).ConfigureAwait(true);

                item.State = observation.State;
                item.CurrentValue = observation.CurrentValueSummary;
                item.RecommendedValue = observation.RecommendedValueSummary;

                foreach (KeyValuePair<string, string> detail in observation.Details)
                {
                    item.Details.Add($"{detail.Key}: {detail.Value}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Detection for {TweakId} threw.", tweak.Descriptor.Id);
                item.CurrentValue = $"Could not be read: {ex.Message}";
            }
        }

        if (applied.TryGetValue(tweak.Descriptor.Id, out string? originalValue))
        {
            item.AppliedByVelocity = true;
            item.OriginalValue = originalValue;
        }

        return item;
    }
}
