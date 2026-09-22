using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Ipc;
using Velocity.Ipc.Protocol;

namespace Velocity.Helper.Handlers;

/// <summary>Answers a liveness check.</summary>
public sealed class PingHandler : IIpcRequestHandler
{
    /// <inheritdoc />
    public string Operation => IpcOperations.Ping;

    /// <inheritdoc />
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(IpcResponse.Ok(request.RequestId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pong"] = "true",
        }));
}

/// <summary>Reports the helper's version and the account it runs as.</summary>
/// <remarks>
/// The desktop application uses this to confirm it is talking to a helper of a compatible version
/// before offering any privileged tweak, rather than discovering a mismatch mid-transaction.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GetIdentityHandler : IIpcRequestHandler
{
    /// <inheritdoc />
    public string Operation => IpcOperations.GetIdentity;

    /// <inheritdoc />
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            ["account"] = identity.Name,
            ["is_system"] = identity.IsSystem.ToString(CultureInfo.InvariantCulture),
            ["protocol"] = "1",
        };

        return Task.FromResult(IpcResponse.Ok(request.RequestId, values));
    }
}
