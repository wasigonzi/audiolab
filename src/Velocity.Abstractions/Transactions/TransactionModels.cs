using System;
using System.Collections.Generic;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Abstractions.Transactions;

/// <summary>Lifecycle state of an optimization transaction.</summary>
public enum TransactionStatus
{
    /// <summary>Created and journalled, no state written yet.</summary>
    Pending = 0,

    /// <summary>
    /// State is being written. A transaction found in this state at startup crashed mid-apply and
    /// is rolled back by the recovery service before the UI is shown.
    /// </summary>
    Applying = 1,

    /// <summary>All steps completed and were verified.</summary>
    Applied = 2,

    /// <summary>Rollback is in progress.</summary>
    RollingBack = 3,

    /// <summary>Every captured value has been written back.</summary>
    RolledBack = 4,

    /// <summary>The apply failed and the transaction was rolled back automatically.</summary>
    FailedAndRolledBack = 5,

    /// <summary>
    /// The apply failed and rollback also failed. The journal is retained and surfaced in the
    /// Restore Center so the user can retry or inspect it.
    /// </summary>
    RollbackFailed = 6,
}

/// <summary>Why a transaction was opened.</summary>
public enum TransactionReason
{
    /// <summary>The user applied one tweak by hand.</summary>
    ManualTweak = 0,

    /// <summary>A profile was applied.</summary>
    ProfileApply = 1,

    /// <summary>A gaming session was started.</summary>
    GamingSession = 2,

    /// <summary>The auto-tune engine is testing a candidate change.</summary>
    AutoTuneTrial = 3,

    /// <summary>The user restored a previous state from the Restore Center.</summary>
    Restore = 4,
}

/// <summary>One captured value inside a snapshot.</summary>
/// <param name="Key">State key that was captured.</param>
/// <param name="Value">Value present before any change.</param>
public readonly record struct SnapshotEntry(StateKey Key, StateValue Value);

/// <summary>
/// The pre change state captured for one step of a transaction.
/// </summary>
/// <remarks>
/// A snapshot is written to the database and committed <em>before</em> the corresponding apply
/// runs. Rollback therefore never depends on anything still being in memory.
/// </remarks>
public sealed record StateSnapshot
{
    /// <summary>Snapshot identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Transaction this snapshot belongs to.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>Tweak whose state was captured.</summary>
    public required string TweakId { get; init; }

    /// <summary>When the capture was taken.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>The captured values.</summary>
    public required IReadOnlyList<SnapshotEntry> Entries { get; init; }
}

/// <summary>One tweak's participation in a transaction.</summary>
public sealed record TransactionStep
{
    /// <summary>Step identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Transaction this step belongs to.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>Zero based order of the step inside the transaction.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Identifier of the tweak that ran.</summary>
    public required string TweakId { get; init; }

    /// <summary>Definition version of the tweak at apply time.</summary>
    public required int TweakDefinitionVersion { get; init; }

    /// <summary>Snapshot captured before the step ran, when one was taken.</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>Outcome of the apply.</summary>
    public ApplyOutcome? ApplyOutcome { get; init; }

    /// <summary>Outcome of the verification.</summary>
    public VerificationStatus? VerificationStatus { get; init; }

    /// <summary>Message recorded from the apply or the failure.</summary>
    public string? Message { get; init; }

    /// <summary>Opaque payload stored for <see cref="ICustomRollback"/>.</summary>
    public string? RollbackPayload { get; init; }

    /// <summary>When the step started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When the step finished, if it did.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary><see langword="true"/> once the step's captured state has been written back.</summary>
    public bool RolledBack { get; init; }
}

/// <summary>
/// A unit of optimization work that is applied and rolled back as a whole.
/// </summary>
public sealed record OptimizationTransaction
{
    /// <summary>Transaction identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required TransactionStatus Status { get; init; }

    /// <summary>Why the transaction was opened.</summary>
    public required TransactionReason Reason { get; init; }

    /// <summary>Optimization profile that drove the transaction, when there was one.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Gaming session the transaction belongs to, when it is session scoped.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Fingerprint of the machine at apply time.</summary>
    public required string HardwareFingerprint { get; init; }

    /// <summary>When the transaction was opened.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When the transaction reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Steps in application order.</summary>
    public IReadOnlyList<TransactionStep> Steps { get; init; } = Array.Empty<TransactionStep>();

    /// <summary>
    /// <see langword="true"/> when the transaction is in a state that requires the recovery
    /// service to act on the next launch.
    /// </summary>
    public bool NeedsRecovery => Status is TransactionStatus.Pending
        or TransactionStatus.Applying
        or TransactionStatus.RollingBack;
}
