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
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Core.Hardware;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Presentation.Mvvm;

namespace Velocity.Presentation.ViewModels;

/// <summary>A live metric rendered as a dashboard card.</summary>
public sealed partial class MetricCardViewModel : ObservableObject
{
    /// <summary>Creates the card.</summary>
    /// <param name="title">Card heading, for example <c>CPU</c>.</param>
    /// <param name="glyph">Icon glyph.</param>
    public MetricCardViewModel(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    /// <summary>Card heading.</summary>
    public string Title { get; }

    /// <summary>Icon glyph.</summary>
    public string Glyph { get; }

    /// <summary>Primary value, already formatted, or a dash when the counter is unavailable.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "—";

    /// <summary>Secondary line giving context, such as the absolute figure behind a percentage.</summary>
    [ObservableProperty]
    public partial string? Detail { get; set; }

    /// <summary>Fill for a gauge, from 0 to 1, or null when there is nothing to show.</summary>
    [ObservableProperty]
    public partial double? Fraction { get; set; }

    /// <summary>
    /// <see langword="true"/> when the platform does not expose this counter. The view renders a
    /// dash rather than a zero: an unavailable counter is not a reading of nothing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; set; } = true;

    /// <summary>Updates the card from a fractional reading.</summary>
    /// <param name="fraction">Value from 0 to 1, or null when unavailable.</param>
    /// <param name="detail">Secondary line.</param>
    public void SetFraction(double? fraction, string? detail = null)
    {
        IsUnavailable = fraction is null;
        Fraction = fraction;
        Detail = detail;
        Value = fraction is null
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{fraction.Value * 100:0}%");
    }

    /// <summary>Updates the card from a preformatted value.</summary>
    /// <param name="value">Value text, or null when unavailable.</param>
    /// <param name="fraction">Optional gauge fill.</param>
    /// <param name="detail">Secondary line.</param>
    public void SetValue(string? value, double? fraction = null, string? detail = null)
    {
        IsUnavailable = value is null;
        Value = value ?? "—";
        Fraction = fraction;
        Detail = detail;
    }
}

/// <summary>
/// The main dashboard: live telemetry, the current optimization state, and the one button that
/// does the work.
/// </summary>
/// <remarks>
/// The dashboard deliberately shows background CPU usage next to total CPU usage. Total utilization
/// tells a gamer almost nothing; what a game loses to other processes is the number this product
/// can actually change.
/// </remarks>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly ISystemMonitor _monitor;
    private readonly ISystemProfileProvider _profileProvider;
    private readonly IOptimizationEngine _engine;
    private readonly ITweakRegistry _registry;
    private readonly IRollbackEngine _rollback;

    /// <summary>Creates the dashboard.</summary>
    /// <param name="monitor">Live telemetry source.</param>
    /// <param name="profileProvider">Machine profile source.</param>
    /// <param name="engine">Optimization engine.</param>
    /// <param name="registry">Tweak catalogue.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="logger">Logger.</param>
    public DashboardViewModel(
        ISystemMonitor monitor,
        ISystemProfileProvider profileProvider,
        IOptimizationEngine engine,
        ITweakRegistry registry,
        IRollbackEngine rollback,
        ILogger<DashboardViewModel> logger)
        : base(logger)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));

        Cards = new ReadOnlyCollection<MetricCardViewModel>(new List<MetricCardViewModel>
        {
            CpuCard, BackgroundCpuCard, GpuCard, MemoryCard, DiskCard, NetworkCard,
        });

        _monitor.SampleProduced += OnSampleProduced;
    }

    /// <summary>Processor utilization.</summary>
    public MetricCardViewModel CpuCard { get; } = new("CPU", "");

    /// <summary>Processor utilization outside the game.</summary>
    public MetricCardViewModel BackgroundCpuCard { get; } = new("Background CPU", "");

    /// <summary>Graphics utilization.</summary>
    public MetricCardViewModel GpuCard { get; } = new("GPU", "");

    /// <summary>Memory utilization.</summary>
    public MetricCardViewModel MemoryCard { get; } = new("Memory", "");

    /// <summary>Disk throughput.</summary>
    public MetricCardViewModel DiskCard { get; } = new("Disk", "");

    /// <summary>Network throughput.</summary>
    public MetricCardViewModel NetworkCard { get; } = new("Network", "");

    /// <summary>All cards, in display order.</summary>
    public IReadOnlyList<MetricCardViewModel> Cards { get; }

    /// <summary>Per logical processor utilization, for the core strip.</summary>
    public ObservableCollection<double> PerCoreUtilization { get; } = new();

    /// <summary>Profiles the user can choose between in Easy Mode.</summary>
    public IReadOnlyList<ProfileKind> AvailableProfiles { get; } =
        new[] { ProfileKind.Competitive, ProfileKind.MaximumFps, ProfileKind.Balanced };

    /// <summary>The selected profile.</summary>
    [ObservableProperty]
    public partial ProfileKind SelectedProfile { get; set; } = ProfileKind.Balanced;

    /// <summary>One-line summary of the machine, shown under the title.</summary>
    [ObservableProperty]
    public partial string MachineSummary { get; set; } = string.Empty;

    /// <summary>Executable of the detected foreground game, when one is running.</summary>
    [ObservableProperty]
    public partial string? ActiveGame { get; set; }

    /// <summary>Whether this product currently has changes applied to the machine.</summary>
    [ObservableProperty]
    public partial bool IsOptimized { get; set; }

    /// <summary>Human readable optimization state, shown next to the main button.</summary>
    [ObservableProperty]
    public partial string OptimizationState { get; set; } = "Not optimized";

    /// <summary>Result lines from the most recent run, shown after the button is pressed.</summary>
    public ObservableCollection<string> LastRunSummary { get; } = new();

    /// <summary>Number of modules the machine supports, shown so the button is not a black box.</summary>
    [ObservableProperty]
    public partial int AvailableModuleCount { get; set; }

    /// <summary>Loads the machine summary and starts telemetry.</summary>
    /// <returns>A task that completes when the dashboard is live.</returns>
    [RelayCommand]
    public Task InitializeAsync() =>
        RunAsync(async token =>
        {
            SystemProfile profile = await _profileProvider.GetAsync(token).ConfigureAwait(true);
            CpuLayout layout = CpuTopologyAnalyzer.Analyze(profile.Cpu);

            MachineSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{profile.Cpu.BrandString} · {profile.Cpu.PhysicalCoreCount}C/{profile.Cpu.LogicalProcessorCount}T · " +
                $"{(profile.Gpus.Count > 0 ? profile.Gpus[0].Description : "no display adapter")} · " +
                $"{profile.OperatingSystem.ProductName} {profile.OperatingSystem.DisplayVersion}");

            AvailableModuleCount = _registry.All.Count;
            _ = layout;

            await _monitor.StartAsync(MonitorCadence.Foreground, token).ConfigureAwait(true);
        },
        "Reading hardware");

    /// <summary>
    /// Applies the selected profile: the one click path.
    /// </summary>
    /// <returns>A task that completes when the run has finished.</returns>
    [RelayCommand]
    public Task OptimizeNowAsync() =>
        RunAsync(async token =>
        {
            LastRunSummary.Clear();

            IReadOnlyList<string> tweakIds = _registry.All
                .Select(tweak => tweak.Descriptor.Id)
                .ToList();

            if (tweakIds.Count == 0)
            {
                LastRunSummary.Add("This build contains no optimization modules yet.");
                return;
            }

            OptimizationRunResult result = await _engine.ApplyAsync(
                new OptimizationRequest
                {
                    TweakIds = tweakIds,
                    Reason = TransactionReason.ProfileApply,
                    ProfileId = SelectedProfile.ToString(),
                    AtomicAllOrNothing = false,
                },
                token).ConfigureAwait(true);

            foreach (TweakRunResult run in result.Results)
            {
                LastRunSummary.Add($"{run.TweakId}: {run.Message}");
            }

            IsOptimized = result.Results.Any(run => run.Outcome is Abstractions.Tweaks.ApplyOutcome.Applied
                or Abstractions.Tweaks.ApplyOutcome.AppliedPendingRestart);

            OptimizationState = result.Status switch
            {
                TransactionStatus.Applied when IsOptimized => "Optimized",
                TransactionStatus.Applied => "Nothing needed changing",
                TransactionStatus.FailedAndRolledBack => "Failed; the machine was put back",
                TransactionStatus.RollbackFailed => "Failed, and some values could not be restored",
                _ => result.Status.ToString(),
            };
        },
        "Applying optimizations");

    /// <summary>Restores the most recent optimization.</summary>
    /// <returns>A task that completes when the rollback has finished.</returns>
    [RelayCommand]
    public Task RestoreAsync() =>
        RunAsync(async token =>
        {
            RollbackResult? result = await _rollback.RollbackLastAsync(token).ConfigureAwait(true);

            LastRunSummary.Clear();

            if (result is null)
            {
                LastRunSummary.Add("There is nothing applied by this product to restore.");
                return;
            }

            LastRunSummary.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Restored {result.RestoredKeyCount} value(s)."));

            foreach (KeyValuePair<string, string> failure in result.Failures)
            {
                LastRunSummary.Add($"Could not restore {failure.Key}: {failure.Value}");
            }

            IsOptimized = false;
            OptimizationState = result.Succeeded ? "Not optimized" : "Partly restored";
        },
        "Restoring");

    /// <summary>Applies a telemetry sample to the cards. Public so the view layer can pump it.</summary>
    /// <param name="sample">Sample to display.</param>
    public void ApplySample(TelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        CpuCard.SetFraction(
            sample.CpuUtilization,
            sample.CpuFrequencyMhz is double mhz
                ? string.Create(CultureInfo.InvariantCulture, $"{mhz:0} MHz")
                : null);

        BackgroundCpuCard.SetFraction(sample.BackgroundCpuUtilization, "outside the game");
        GpuCard.SetFraction(
            sample.GpuUtilization,
            sample.VideoMemoryUsedBytes is long vram ? $"{FormatBytes(vram)} VRAM" : null);

        MemoryCard.SetFraction(
            sample.MemoryUtilization,
            sample.MemoryUsedBytes is long used && sample.MemoryTotalBytes is long total
                ? $"{FormatBytes(used)} of {FormatBytes(total)}"
                : null);

        DiskCard.SetValue(
            sample.DiskBytesPerSecond is double disk ? $"{FormatBytes((long)disk)}/s" : null,
            detail: sample.DiskQueueLength is double queue
                ? string.Create(CultureInfo.InvariantCulture, $"queue {queue:0.0}")
                : null);

        NetworkCard.SetValue(
            sample.NetworkBytesPerSecond is double network ? $"{FormatBytes((long)network)}/s" : null);

        ActiveGame = sample.ActiveGameExecutable;

        if (sample.PerCoreUtilization is { Count: > 0 } perCore)
        {
            if (PerCoreUtilization.Count != perCore.Count)
            {
                PerCoreUtilization.Clear();
                foreach (double value in perCore)
                {
                    PerCoreUtilization.Add(value);
                }
            }
            else
            {
                for (int i = 0; i < perCore.Count; i++)
                {
                    PerCoreUtilization[i] = perCore[i];
                }
            }
        }
    }

    private void OnSampleProduced(object? sender, TelemetrySample sample) => ApplySample(sample);

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        // The monitor outlives the page, so leaving this subscription in place would keep a
        // disposed view model alive and updating for the rest of the session.
        _monitor.SampleProduced -= OnSampleProduced;
        base.DisposeCore();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
