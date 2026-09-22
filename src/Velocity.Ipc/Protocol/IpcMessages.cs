using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Velocity.Ipc.Protocol;

/// <summary>Well known operation names understood by the privileged helper.</summary>
/// <remarks>
/// Operations are strings rather than an enum so that a newer UI talking to an older helper gets a
/// clean "unknown operation" refusal instead of a deserialization failure. Every name here must
/// also appear in the authorizer's allow list; a handler alone is not enough to make an operation
/// reachable.
/// </remarks>
public static class IpcOperations
{
    /// <summary>Liveness check. Takes no arguments.</summary>
    public const string Ping = "ping";

    /// <summary>Returns the helper's version and the privileges it is running with.</summary>
    public const string GetIdentity = "get-identity";

    /// <summary>Reads one registry value. Arguments: <c>hive</c>, <c>path</c>, <c>name</c>.</summary>
    public const string RegistryRead = "registry.read";

    /// <summary>
    /// Writes or deletes one registry value.
    /// Arguments: <c>hive</c>, <c>path</c>, <c>name</c>, <c>kind</c>, <c>data</c>.
    /// </summary>
    public const string RegistryWrite = "registry.write";
}

/// <summary>Error codes a helper can return.</summary>
public static class IpcErrorCodes
{
    /// <summary>The operation name is not in the allow list.</summary>
    public const string UnknownOperation = "unknown-operation";

    /// <summary>The arguments failed validation.</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>The operation targets state the helper refuses to touch.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The request was rejected as a replay or out of order message.</summary>
    public const string RejectedSequence = "rejected-sequence";

    /// <summary>The handler threw.</summary>
    public const string OperationFailed = "operation-failed";
}

/// <summary>A request sent from the desktop process to the privileged helper.</summary>
public sealed record IpcRequest
{
    /// <summary>Correlates the response with this request.</summary>
    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    /// <summary>
    /// Strictly increasing per connection. The helper rejects a sequence number it has already
    /// seen, so a captured frame cannot be replayed on the same connection.
    /// </summary>
    [JsonPropertyName("seq")]
    public required long Sequence { get; init; }

    /// <summary>Operation to perform.</summary>
    [JsonPropertyName("op")]
    public required string Operation { get; init; }

    /// <summary>Operation arguments.</summary>
    [JsonPropertyName("args")]
    public Dictionary<string, string> Arguments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the caller issued the request, used to reject stale frames.</summary>
    [JsonPropertyName("issued")]
    public required DateTimeOffset IssuedAtUtc { get; init; }
}

/// <summary>A response from the privileged helper.</summary>
public sealed record IpcResponse
{
    /// <summary>Identifier of the request this answers.</summary>
    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("ok")]
    public required bool Success { get; init; }

    /// <summary>Machine readable error code when <see cref="Success"/> is false.</summary>
    [JsonPropertyName("code")]
    public string? ErrorCode { get; init; }

    /// <summary>Human readable error detail when <see cref="Success"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? ErrorMessage { get; init; }

    /// <summary>Values returned by the operation.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a successful response.</summary>
    /// <param name="requestId">Request being answered.</param>
    /// <param name="values">Values to return.</param>
    /// <returns>The response.</returns>
    public static IpcResponse Ok(string requestId, Dictionary<string, string>? values = null) => new()
    {
        RequestId = requestId,
        Success = true,
        Values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Creates a failure response.</summary>
    /// <param name="requestId">Request being answered.</param>
    /// <param name="code">Machine readable error code.</param>
    /// <param name="message">Human readable detail.</param>
    /// <returns>The response.</returns>
    public static IpcResponse Fail(string requestId, string code, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        ErrorCode = code,
        ErrorMessage = message,
    };
}

/// <summary>Source generated serialization for the IPC protocol.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
