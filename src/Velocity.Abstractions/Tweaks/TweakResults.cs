using System;
using System.Collections.Generic;
using Velocity.Abstractions.State;

namespace Velocity.Abstractions.Tweaks;

/// <summary>Result of a compatibility evaluation.</summary>
/// <param name="Status">Whether the tweak may run.</param>
/// <param name="Reason">Plain language reason, shown to the user when not supported.</param>
public readonly record struct CompatibilityResult(CompatibilityStatus Status, string Reason)
{
    /// <summary><see langword="true"/> when the tweak may be applied.</summary>
    public bool IsSupported => Status == CompatibilityStatus.Supported;

    /// <summary>A supported result.</summary>
    /// <param name="reason">Optional note shown in Expert Mode.</param>
    /// <returns>The result.</returns>
    public static CompatibilityResult Supported(string reason = "Supported on this system.") =>
        new(CompatibilityStatus.Supported, reason);

    /// <summary>An unsupported result.</summary>
    /// <param name="status">Why the tweak cannot run.</param>
    /// <param name="reason">Plain language reason.</param>
    /// <returns>The result.</returns>
    public static CompatibilityResult Unsupported(CompatibilityStatus status, string reason) =>
        new(status, reason);
}

/// <summary>What the tweak observed on the machine before any change.</summary>
public sealed record TweakObservation
{
    /// <summary>Whether the machine is currently in the tweaked state.</summary>
    public required AppliedState State { get; init; }

    /// <summary>One line description of the current configuration, for the tweak list.</summary>
    public required string CurrentValueSummary { get; init; }

    /// <summary>One line description of what this tweak would change it to.</summary>
    public required string RecommendedValueSummary { get; init; }

    /// <summary>
    /// Raw values behind the summaries, shown verbatim in Expert Mode so that the technical user
    /// can confirm the tweak reads what it claims to read.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Result of applying a tweak.</summary>
public sealed record ApplyResult
{
    /// <summary>What happened.</summary>
    public required ApplyOutcome Outcome { get; init; }

    /// <summary>Message for the log and, on failure, for the user.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// State keys the tweak actually changed. The transaction coordinator cross checks these
    /// against the keys that were snapshotted and fails the transaction if a tweak wrote
    /// something it never declared.
    /// </summary>
    public IReadOnlyList<StateKey> ChangedKeys { get; init; } = Array.Empty<StateKey>();

    /// <summary>Exception captured when <see cref="Outcome"/> is <see cref="ApplyOutcome.Failed"/>.</summary>
    public Exception? Error { get; init; }

    /// <summary>A successful result.</summary>
    /// <param name="message">Log message.</param>
    /// <param name="changedKeys">Keys that were written.</param>
    /// <returns>The result.</returns>
    public static ApplyResult Applied(string message, params StateKey[] changedKeys) =>
        new() { Outcome = ApplyOutcome.Applied, Message = message, ChangedKeys = changedKeys };

    /// <summary>A result meaning the machine was already in the target state.</summary>
    /// <param name="message">Log message.</param>
    /// <returns>The result.</returns>
    public static ApplyResult NoChange(string message) =>
        new() { Outcome = ApplyOutcome.NoChangeRequired, Message = message };

    /// <summary>A failed result.</summary>
    /// <param name="message">Log message.</param>
    /// <param name="error">Underlying exception, when there was one.</param>
    /// <returns>The result.</returns>
    public static ApplyResult Failed(string message, Exception? error = null) =>
        new() { Outcome = ApplyOutcome.Failed, Message = message, Error = error };
}

/// <summary>Result of verifying that an applied tweak took effect.</summary>
/// <param name="Status">Verification outcome.</param>
/// <param name="Message">Explanation for the log and Expert Mode.</param>
public readonly record struct VerificationResult(VerificationStatus Status, string Message)
{
    /// <summary><see langword="true"/> when the applied state was confirmed or is pending a restart.</summary>
    public bool IsAcceptable => Status is VerificationStatus.Verified
        or VerificationStatus.PendingRestart
        or VerificationStatus.NotVerifiable;

    /// <summary>A verified result.</summary>
    /// <param name="message">Explanation.</param>
    /// <returns>The result.</returns>
    public static VerificationResult Verified(string message = "Observed state matches the requested state.") =>
        new(VerificationStatus.Verified, message);

    /// <summary>A mismatched result.</summary>
    /// <param name="message">Explanation.</param>
    /// <returns>The result.</returns>
    public static VerificationResult Mismatch(string message) =>
        new(VerificationStatus.Mismatch, message);
}
