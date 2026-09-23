using System.Threading;
using System.Threading.Tasks;
using Velocity.Ipc.Protocol;

namespace Velocity.Ipc;

/// <summary>Handles one privileged operation inside the helper.</summary>
/// <remarks>
/// A handler is reached only when the dispatcher has already accepted the operation name and the
/// message sequence. Authorization of the operation's <em>arguments</em> is the handler's
/// responsibility, because only the handler knows what they mean.
/// </remarks>
public interface IIpcRequestHandler
{
    /// <summary>Operation name this handler serves, matching a constant on <see cref="IpcOperations"/>.</summary>
    string Operation { get; }

    /// <summary>Performs the operation.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Token used to abort the operation.</param>
    /// <returns>The response to send back.</returns>
    Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken);
}
