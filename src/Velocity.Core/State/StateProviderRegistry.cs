using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Velocity.Abstractions.State;

namespace Velocity.Core.State;

/// <summary>Default <see cref="IStateProviderRegistry"/> backed by the DI registered providers.</summary>
public sealed class StateProviderRegistry : IStateProviderRegistry
{
    private readonly Dictionary<string, IStateProvider> _providers;

    /// <summary>Creates the registry.</summary>
    /// <param name="providers">Providers registered for this platform.</param>
    /// <exception cref="ArgumentException">Two providers declare the same scheme.</exception>
    public StateProviderRegistry(IEnumerable<IStateProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = new Dictionary<string, IStateProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (IStateProvider provider in providers)
        {
            if (!_providers.TryAdd(provider.ProviderId, provider))
            {
                throw new ArgumentException(
                    $"More than one state provider claims the scheme '{provider.ProviderId}'.",
                    nameof(providers));
            }
        }

        Providers = new ReadOnlyCollection<IStateProvider>(_providers.Values.ToList());
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IStateProvider> Providers { get; }

    /// <inheritdoc />
    public IStateProvider Resolve(StateKey key) =>
        _providers.TryGetValue(key.Provider, out IStateProvider? provider)
            ? provider
            : throw new InvalidOperationException(
                $"No state provider is registered for scheme '{key.Provider}' (key '{key}'). " +
                "This usually means a tweak is running on a platform its provider does not support.");

    /// <inheritdoc />
    public bool TryResolve(StateKey key, out IStateProvider? provider) =>
        _providers.TryGetValue(key.Provider, out provider);
}
