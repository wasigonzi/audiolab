using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.State;

/// <summary>
/// Reads and writes one class of system state so that the snapshot and rollback engines never
/// need to know what kind of setting they are handling.
/// </summary>
public interface IStateProvider
{
    /// <summary>Scheme this provider serves, matching <see cref="StateKey.Provider"/>.</summary>
    string ProviderId { get; }

    /// <summary><see langword="true"/> when the provider can write as well as read.</summary>
    bool CanWrite { get; }

    /// <summary>Determines whether writing the key needs the privileged helper.</summary>
    /// <param name="key">Key that would be written.</param>
    /// <returns><see langword="true"/> when elevation is required.</returns>
    bool RequiresElevation(StateKey key);

    /// <summary>Reads the current value of a key.</summary>
    /// <param name="key">Key to read.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The current value, or <see cref="StateValue.Absent"/> when it does not exist.</returns>
    Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken);

    /// <summary>Writes a value, creating or deleting the item as required.</summary>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value to write. <see cref="StateValueKind.Absent"/> deletes the item.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the write has been made.</returns>
    Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken);
}

/// <summary>Resolves the provider responsible for a key.</summary>
public interface IStateProviderRegistry
{
    /// <summary>Returns the provider for a key.</summary>
    /// <param name="key">Key whose provider is wanted.</param>
    /// <returns>The registered provider.</returns>
    /// <exception cref="System.InvalidOperationException">No provider is registered for the scheme.</exception>
    IStateProvider Resolve(StateKey key);

    /// <summary>Attempts to resolve the provider for a key.</summary>
    /// <param name="key">Key whose provider is wanted.</param>
    /// <param name="provider">The resolved provider, when one is registered.</param>
    /// <returns><see langword="true"/> when a provider is registered for the scheme.</returns>
    bool TryResolve(StateKey key, out IStateProvider? provider);

    /// <summary>All registered providers.</summary>
    IReadOnlyCollection<IStateProvider> Providers { get; }
}
