using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Ipc.Protocol;

namespace Velocity.Platform.Windows.Ipc;

/// <summary>The desktop side of the privileged helper channel.</summary>
public interface IPrivilegedChannel
{
    /// <summary>Whether the helper can currently be reached.</summary>
    bool IsAvailable { get; }

    /// <summary>Invokes a privileged operation.</summary>
    /// <param name="operation">Operation name.</param>
    /// <param name="arguments">Operation arguments.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The helper's response.</returns>
    Task<IpcResponse> InvokeAsync(
        string operation,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken);
}
