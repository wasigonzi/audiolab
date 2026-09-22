using System;
using System.Collections.Generic;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Core.Transactions;

/// <summary>A request to apply a set of tweaks as one transaction.</summary>
public sealed record OptimizationRequest
{
    /// <summary>Tweaks to apply, in the order given.</summary>
    public required IReadOnlyList<string> TweakIds { get; init; }

    /// <summary>Why the transaction is being opened.</summary>
    public required TransactionReason Reason { get; init; }

    /// <summary>Profile driving the request, when there is one.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Gaming session the transaction belongs to, when it is session scoped.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Per tweak options, keyed by tweak id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Options { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When <see langword="true"/> (the default) a failing step rolls the whole transaction back.
    /// When <see langword="false"/> the remaining steps still run and only the failed step is
    /// reversed, which is what a profile apply wants when one unrelated tweak is unavailable.
    /// </summary>
    public bool AtomicAllOrNothing { get; init; } = true;
}

/// <summary>Outcome of one tweak inside a transaction.</summary>
public sealed record TweakRunResult
{
    /// <summary>Tweak identifier.</summary>
    public required string TweakId { get; init; }

    /// <summary>Compatibility result evaluated before the tweak ran.</summary>
    public required CompatibilityResult Compatibility { get; init; }

    /// <summary>Apply outcome, or <see langword="null"/> when the tweak never ran.</summary>
    public ApplyOutcome? Outcome { get; init; }

    /// <summary>Verification result, or <see langword="null"/> when no apply happened.</summary>
    public VerificationStatus? Verification { get; init; }

    /// <summary>What the machine looked like before the change.</summary>
    public TweakObservation? Observation { get; init; }

    /// <summary>Explanation for the log and the UI.</summary>
    public required string Message { get; init; }

    /// <summary>State keys that were actually written.</summary>
    public IReadOnlyList<string> ChangedKeys { get; init; } = Array.Empty<string>();
}

/// <summary>Outcome of a whole optimization run.</summary>
public sealed record OptimizationRunResult
{
    /// <summary>Transaction that was opened.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>Final transaction state.</summary>
    public required TransactionStatus Status { get; init; }

    /// <summary>Per tweak outcomes, in application order.</summary>
    public required IReadOnlyList<TweakRunResult> Results { get; init; }

    /// <summary><see langword="true"/> when the transaction ended in the applied state.</summary>
    public bool Succeeded => Status == TransactionStatus.Applied;
}
