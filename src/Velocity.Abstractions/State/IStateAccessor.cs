using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.State;

/// <summary>
/// The only way a tweak is allowed to touch system state.
/// </summary>
/// <remarks>
/// Routing every read and write through this facade is what makes the guarantees of the
/// transaction system real rather than aspirational: the accessor records each write against the
/// open transaction, refuses writes to keys the tweak did not declare, and transparently forwards
/// privileged writes to the helper service so that the UI process never needs to be elevated.
/// </remarks>
public interface IStateAccessor
{
    /// <summary>Reads a value.</summary>
    /// <param name="key">Key to read.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The current value, or <see cref="StateValue.Absent"/>.</returns>
    Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken);

    /// <summary>Reads several values in one call.</summary>
    /// <param name="keys">Keys to read.</param>
    /// <param name="cancellationToken">Token used to abort the reads.</param>
    /// <returns>The values, keyed by their state key.</returns>
    Task<IReadOnlyDictionary<StateKey, StateValue>> ReadManyAsync(
        IEnumerable<StateKey> keys,
        CancellationToken cancellationToken);

    /// <summary>Writes a value, recording it against the open transaction.</summary>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value to write.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the write has been made.</returns>
    Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken);
}
