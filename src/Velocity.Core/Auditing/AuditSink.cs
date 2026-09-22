using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Diagnostics;
using Velocity.Data.Repositories;

namespace Velocity.Core.Auditing;

/// <summary>Writes durable audit records for everything the engine changes.</summary>
public interface IAuditSink
{
    /// <summary>Records an operation.</summary>
    /// <param name="record">The record to write. Text fields are redacted before storage.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the record has been stored.</returns>
    Task RecordAsync(OperationAuditRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IAuditSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Redaction happens here rather than at each call site, because registry paths under
/// <c>HKEY_USERS</c> contain a user SID and file paths contain the account name.
/// </para>
/// <para>
/// A failure to write an audit record is logged but never propagated: losing an audit line is bad,
/// but failing an in-flight rollback because the audit table is locked would be worse.
/// </para>
/// </remarks>
public sealed class AuditSink : IAuditSink
{
    private readonly IAuditRepository _repository;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly ILogger<AuditSink> _logger;

    /// <summary>Creates the sink.</summary>
    /// <param name="repository">Audit storage.</param>
    /// <param name="redactor">Redactor applied to every text field.</param>
    /// <param name="logger">Logger.</param>
    public AuditSink(IAuditRepository repository, ISensitiveDataRedactor redactor, ILogger<AuditSink> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RecordAsync(OperationAuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await _repository.WriteAsync(Redact(record), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to write audit record for {Module}/{Action}.", record.Module, record.Action);
        }
    }

    private OperationAuditRecord Redact(OperationAuditRecord record)
    {
        Dictionary<string, string>? details = null;
        if (record.Details.Count > 0)
        {
            details = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> detail in record.Details)
            {
                details[detail.Key] = _redactor.Redact(detail.Value);
            }
        }

        return record with
        {
            Target = RedactOrNull(record.Target),
            OriginalValue = RedactOrNull(record.OriginalValue),
            NewValue = RedactOrNull(record.NewValue),
            Error = RedactOrNull(record.Error),
            Details = details ?? record.Details,
        };
    }

    private string? RedactOrNull(string? value) => value is null ? null : _redactor.Redact(value);
}

/// <summary>Convenience factory methods for audit records.</summary>
public static class AuditRecordFactory
{
    /// <summary>Creates a successful record.</summary>
    /// <param name="module">Module that performed the action.</param>
    /// <param name="action">Action performed.</param>
    /// <param name="target">Subject of the action.</param>
    /// <param name="transactionId">Owning transaction, when there is one.</param>
    /// <param name="originalValue">Value before the action.</param>
    /// <param name="newValue">Value after the action.</param>
    /// <returns>The record.</returns>
    public static OperationAuditRecord Success(
        string module,
        string action,
        string? target = null,
        Guid? transactionId = null,
        string? originalValue = null,
        string? newValue = null) => new()
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            Module = module,
            Action = action,
            Target = target,
            TransactionId = transactionId,
            OriginalValue = originalValue,
            NewValue = newValue,
            Outcome = AuditOutcome.Success,
        };

    /// <summary>Creates a failure record.</summary>
    /// <param name="module">Module that performed the action.</param>
    /// <param name="action">Action attempted.</param>
    /// <param name="error">Failure detail.</param>
    /// <param name="target">Subject of the action.</param>
    /// <param name="transactionId">Owning transaction, when there is one.</param>
    /// <returns>The record.</returns>
    public static OperationAuditRecord Failure(
        string module,
        string action,
        string error,
        string? target = null,
        Guid? transactionId = null) => new()
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            Module = module,
            Action = action,
            Target = target,
            TransactionId = transactionId,
            Outcome = AuditOutcome.Failure,
            Error = error,
        };

    /// <summary>Creates a record for an operation refused by policy.</summary>
    /// <param name="module">Module that refused the action.</param>
    /// <param name="action">Action that was refused.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <param name="target">Subject of the action.</param>
    /// <returns>The record.</returns>
    public static OperationAuditRecord Denied(
        string module,
        string action,
        string reason,
        string? target = null) => new()
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            Module = module,
            Action = action,
            Target = target,
            Outcome = AuditOutcome.Denied,
            Error = reason,
        };
}
