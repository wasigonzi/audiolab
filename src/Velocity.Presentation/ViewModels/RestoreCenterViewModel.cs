using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hosting;
using Velocity.Abstractions.Transactions;
using Velocity.Core.Transactions;
using Velocity.Data;
using Velocity.Data.Repositories;
using Velocity.Presentation.Mvvm;

namespace Velocity.Presentation.ViewModels;

/// <summary>One entry in the Restore Center list.</summary>
public sealed partial class RestorePointViewModel : ObservableObject
{
    /// <summary>Creates the entry from a journalled transaction.</summary>
    /// <param name="transaction">The transaction.</param>
    public RestorePointViewModel(OptimizationTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        Id = transaction.Id;
        StartedAtUtc = transaction.StartedAtUtc;
        Status = transaction.Status;
        Reason = transaction.Reason;
        ProfileId = transaction.ProfileId;

        Steps = new ReadOnlyCollection<string>(transaction.Steps
            .Select(DescribeStep)
            .ToList());

        ChangedCount = transaction.Steps.Count(step =>
            step.ApplyOutcome == Abstractions.Tweaks.ApplyOutcome.Applied ||
            step.ApplyOutcome == Abstractions.Tweaks.ApplyOutcome.AppliedPendingRestart);

        CanRestore = transaction.Status is TransactionStatus.Applied or TransactionStatus.RollbackFailed
                     && transaction.Steps.Any(step => !step.RolledBack);
    }

    private static string DescribeStep(TransactionStep step)
    {
        string outcome = step.ApplyOutcome?.ToString() ?? "not run";
        string restored = step.RolledBack ? " (restored)" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{step.Ordinal + 1}. {step.TweakId} — {outcome}{restored}");
    }

    /// <summary>Transaction identifier.</summary>
    public Guid Id { get; }

    /// <summary>When the change was made.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Lifecycle state.</summary>
    public TransactionStatus Status { get; }

    /// <summary>Why the transaction was opened.</summary>
    public TransactionReason Reason { get; }

    /// <summary>Profile that drove it, when there was one.</summary>
    public string? ProfileId { get; }

    /// <summary>One line per step.</summary>
    public IReadOnlyList<string> Steps { get; }

    /// <summary>How many steps actually changed something.</summary>
    public int ChangedCount { get; }

    /// <summary>Whether there is anything left to put back.</summary>
    public bool CanRestore { get; }
}

/// <summary>
/// The Restore Center: everything this product has changed, and how to put it back.
/// </summary>
/// <remarks>
/// The list is built from the transaction journal rather than from a separate "restore point"
/// concept, so it can never disagree with what would actually be restored.
/// </remarks>
public sealed partial class RestoreCenterViewModel : ViewModelBase
{
    private readonly ITransactionJournal _journal;
    private readonly IRollbackEngine _rollback;
    private readonly IAppliedTweakReader _appliedTweaks;
    private readonly IVelocityPaths _paths;

    /// <summary>Creates the page.</summary>
    /// <param name="journal">Transaction journal.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="appliedTweaks">Reader for what is currently applied.</param>
    /// <param name="paths">Filesystem layout, used for snapshot export.</param>
    /// <param name="logger">Logger.</param>
    public RestoreCenterViewModel(
        ITransactionJournal journal,
        IRollbackEngine rollback,
        IAppliedTweakReader appliedTweaks,
        IVelocityPaths paths,
        ILogger<RestoreCenterViewModel> logger)
        : base(logger)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _appliedTweaks = appliedTweaks ?? throw new ArgumentNullException(nameof(appliedTweaks));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>Recent transactions, newest first.</summary>
    public ObservableCollection<RestorePointViewModel> RestorePoints { get; } = new();

    /// <summary>Tweaks this product currently has applied, with the value captured beforehand.</summary>
    public ObservableCollection<string> CurrentlyApplied { get; } = new();

    /// <summary>Message shown when the product has never changed anything.</summary>
    [ObservableProperty]
    public partial string? EmptyMessage { get; set; }

    /// <summary>Path of the most recently exported snapshot bundle.</summary>
    [ObservableProperty]
    public partial string? LastExportPath { get; set; }

    /// <summary>Loads the history.</summary>
    /// <returns>A task that completes when the page is populated.</returns>
    [RelayCommand]
    public Task LoadAsync() =>
        RunAsync(async token =>
        {
            RestorePoints.Clear();
            CurrentlyApplied.Clear();

            IReadOnlyList<OptimizationTransaction> transactions =
                await _journal.GetRecentTransactionsAsync(50, token).ConfigureAwait(true);

            foreach (OptimizationTransaction transaction in transactions)
            {
                RestorePoints.Add(new RestorePointViewModel(transaction));
            }

            EmptyMessage = transactions.Count == 0
                ? "This product has not changed anything on this machine."
                : null;

            IReadOnlyDictionary<string, string?> applied =
                await _appliedTweaks.GetAppliedOriginalValuesAsync(token).ConfigureAwait(true);

            foreach (KeyValuePair<string, string?> entry in applied.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                CurrentlyApplied.Add($"{entry.Key} — original value: {entry.Value ?? "(not set)"}");
            }
        },
        "Reading change history");

    /// <summary>Restores one transaction.</summary>
    /// <param name="point">Entry to restore.</param>
    /// <returns>A task that completes when the rollback has finished.</returns>
    [RelayCommand]
    public Task RestoreAsync(RestorePointViewModel? point) =>
        point is null
            ? Task.CompletedTask
            : RunAsync(async token =>
                {
                    RollbackResult result = await _rollback
                        .RollbackTransactionAsync(point.Id, token)
                        .ConfigureAwait(true);

                    if (!result.Succeeded)
                    {
                        ErrorMessage = string.Join(
                            "; ", result.Failures.Select(failure => $"{failure.Key}: {failure.Value}"));
                    }

                    await LoadAsync().ConfigureAwait(true);
                },
                "Restoring");

    /// <summary>Restores everything this product has applied and not yet undone.</summary>
    /// <returns>A task that completes when every rollback has finished.</returns>
    [RelayCommand]
    public Task RestoreAllAsync() =>
        RunAsync(async token =>
        {
            var failures = new List<string>();

            // Newest first: a later transaction may have written over an earlier one's key, so
            // undoing in reverse chronological order lands on the oldest captured value.
            foreach (RestorePointViewModel point in RestorePoints.Where(point => point.CanRestore).ToList())
            {
                token.ThrowIfCancellationRequested();

                RollbackResult result =
                    await _rollback.RollbackTransactionAsync(point.Id, token).ConfigureAwait(true);

                if (!result.Succeeded)
                {
                    failures.AddRange(result.Failures.Select(failure => $"{failure.Key}: {failure.Value}"));
                }
            }

            if (failures.Count > 0)
            {
                ErrorMessage = string.Join("; ", failures);
            }

            await LoadAsync().ConfigureAwait(true);
        },
        "Restoring everything");

    /// <summary>
    /// Writes a transaction's captured values to a portable JSON bundle.
    /// </summary>
    /// <param name="point">Entry to export.</param>
    /// <returns>A task that completes when the bundle has been written.</returns>
    [RelayCommand]
    public Task ExportAsync(RestorePointViewModel? point) =>
        point is null
            ? Task.CompletedTask
            : RunAsync(async token =>
                {
                    OptimizationTransaction? transaction =
                        await _journal.GetTransactionAsync(point.Id, token).ConfigureAwait(true);

                    if (transaction is null)
                    {
                        ErrorMessage = "That change is no longer in the journal.";
                        return;
                    }

                    var snapshots = new List<StateSnapshot>();
                    foreach (TransactionStep step in transaction.Steps)
                    {
                        if (step.SnapshotId is Guid snapshotId)
                        {
                            StateSnapshot? snapshot =
                                await _journal.GetSnapshotAsync(snapshotId, token).ConfigureAwait(true);

                            if (snapshot is not null)
                            {
                                snapshots.Add(snapshot);
                            }
                        }
                    }

                    _paths.EnsureCreated();
                    string path = System.IO.Path.Combine(
                        _paths.SnapshotDirectory,
                        string.Create(CultureInfo.InvariantCulture, $"snapshot-{transaction.Id:N}.json"));

                    await System.IO.File.WriteAllTextAsync(
                        path,
                        JsonSerializer.Serialize(
                            new SnapshotBundle(transaction, snapshots), VelocityJson.IndentedOptions),
                        token).ConfigureAwait(true);

                    LastExportPath = path;
                },
                "Exporting snapshot");

    /// <summary>A transaction and its captured values, as written to an export bundle.</summary>
    /// <param name="Transaction">The transaction, including its steps.</param>
    /// <param name="Snapshots">Captured values, one snapshot per step that took one.</param>
    public sealed record SnapshotBundle(
        OptimizationTransaction Transaction,
        IReadOnlyList<StateSnapshot> Snapshots);
}
