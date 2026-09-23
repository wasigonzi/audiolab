using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;

namespace Velocity.Core.State;

/// <summary>
/// The state accessor handed to a tweak while a transaction step is running.
/// </summary>
/// <remarks>
/// <para>
/// Three invariants are enforced here rather than trusted to each tweak:
/// </para>
/// <list type="number">
/// <item>a tweak may only write keys it declared, because only declared keys were snapshotted;</item>
/// <item>a write that needs elevation fails loudly when no privileged channel exists, instead of
/// silently doing nothing and reporting success;</item>
/// <item>every write is recorded, so the transaction coordinator can cross check what a tweak
/// claims it changed against what it actually changed.</item>
/// </list>
/// </remarks>
public sealed class TransactionalStateAccessor : IStateAccessor
{
    private readonly IStateProviderRegistry _registry;
    private readonly IPrivilegeContext _privileges;
    private readonly HashSet<string> _declaredKeys;
    private readonly List<StateKey> _writtenKeys = new();
    private readonly string _tweakId;

    /// <summary>Creates an accessor scoped to one transaction step.</summary>
    /// <param name="registry">Providers available on this platform.</param>
    /// <param name="privileges">Privileges available to the current process.</param>
    /// <param name="tweakId">Tweak the accessor is scoped to.</param>
    /// <param name="declaredKeys">Keys the tweak declared and that were snapshotted.</param>
    public TransactionalStateAccessor(
        IStateProviderRegistry registry,
        IPrivilegeContext privileges,
        string tweakId,
        IEnumerable<StateKey> declaredKeys)
    {
        ArgumentNullException.ThrowIfNull(declaredKeys);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
        _tweakId = tweakId ?? throw new ArgumentNullException(nameof(tweakId));

        _declaredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StateKey key in declaredKeys)
        {
            _declaredKeys.Add(key.ToString());
        }
    }

    /// <summary>Keys this accessor has actually written, in write order.</summary>
    public IReadOnlyList<StateKey> WrittenKeys => _writtenKeys;

    /// <inheritdoc />
    public Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken) =>
        _registry.Resolve(key).ReadAsync(key, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<StateKey, StateValue>> ReadManyAsync(
        IEnumerable<StateKey> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var values = new Dictionary<StateKey, StateValue>();
        foreach (StateKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[key] = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return values;
    }

    /// <inheritdoc />
    public async Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        if (!_declaredKeys.Contains(key.ToString()))
        {
            throw new UndeclaredStateWriteException(_tweakId, key);
        }

        IStateProvider provider = _registry.Resolve(key);

        if (!provider.CanWrite)
        {
            throw new InvalidOperationException(
                $"State provider '{provider.ProviderId}' is read only; '{key}' cannot be written.");
        }

        if (provider.RequiresElevation(key) && !_privileges.CanElevate)
        {
            throw new PrivilegeRequiredException(key);
        }

        await provider.WriteAsync(key, value, cancellationToken).ConfigureAwait(false);
        _writtenKeys.Add(key);
    }
}
