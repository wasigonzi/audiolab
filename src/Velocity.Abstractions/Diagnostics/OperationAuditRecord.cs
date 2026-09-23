using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Diagnostics;

/// <summary>Outcome recorded for an audited operation.</summary>
public enum AuditOutcome
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,

    /// <summary>The operation failed.</summary>
    Failure = 1,

    /// <summary>The operation was refused by policy, for example an unauthorised privileged call.</summary>
    Denied = 2,
}

/// <summary>
/// One durable audit entry.
/// </summary>
/// <remarks>
/// Audit records answer "what did this program change on my machine, and did it change it back".
/// They are written to the database rather than only to text logs so the Restore Center can query
/// them, and they never contain user names, paths under the user profile, or adapter hardware
/// addresses: those are redacted before the record is created.
/// </remarks>
public sealed record OperationAuditRecord
{
    /// <summary>Record identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>When the operation happened.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Module that performed it, for example <c>Core.TransactionCoordinator</c>.</summary>
    public required string Module { get; init; }

    /// <summary>Action performed, for example <c>apply</c>, <c>rollback</c>, <c>privileged-write</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Subject of the action, typically a tweak id or a state key.</summary>
    public string? Target { get; init; }

    /// <summary>Value before the action, rendered for display.</summary>
    public string? OriginalValue { get; init; }

    /// <summary>Value after the action, rendered for display.</summary>
    public string? NewValue { get; init; }

    /// <summary>Outcome.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>Transaction the action belonged to, when there was one.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>Error detail when <see cref="Outcome"/> is not success.</summary>
    public string? Error { get; init; }

    /// <summary>Additional structured detail, serialized as JSON when persisted.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
