using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.State;

namespace Velocity.Core.State;

/// <summary>
/// A state provider that keeps values in memory.
/// </summary>
/// <remarks>
/// Used for two real purposes: the dry run mode, where the whole engine executes against a copy of
/// the machine's values so a profile can be previewed without touching the system, and the test
/// suite, which runs the genuine transaction, verification and rollback paths against it.
/// </remarks>
public sealed class InMemoryStateProvider : IStateProvider
{
    private readonly ConcurrentDictionary<string, StateValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _elevatedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a provider for the given scheme.</summary>
    /// <param name="providerId">Scheme this provider answers for.</param>
    /// <param name="canWrite">Whether writes are permitted.</param>
    public InMemoryStateProvider(string providerId = "memory", bool canWrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ProviderId = providerId;
        CanWrite = canWrite;
    }

    /// <inheritdoc />
    public string ProviderId { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    /// <summary>Number of writes performed, used by tests to assert that no write happened.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Seeds a value as if it already existed on the machine.</summary>
    /// <param name="key">Key to seed.</param>
    /// <param name="value">Value to seed.</param>
    public void Seed(StateKey key, StateValue value) => _values[key.ToString()] = value;

    /// <summary>Marks a key as requiring elevation to write.</summary>
    /// <param name="key">Key that requires elevation.</param>
    public void RequireElevation(StateKey key) => _elevatedKeys.Add(key.ToString());

    /// <inheritdoc />
    public bool RequiresElevation(StateKey key) => _elevatedKeys.Contains(key.ToString());

    /// <inheritdoc />
    public Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key.ToString(), out StateValue value) ? value : StateValue.Absent);

    /// <inheritdoc />
    public Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException($"Provider '{ProviderId}' is read only.");
        }

        WriteCount++;

        if (value.IsAbsent)
        {
            _values.TryRemove(key.ToString(), out _);
        }
        else
        {
            _values[key.ToString()] = value;
        }

        return Task.CompletedTask;
    }
}
