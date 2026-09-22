using System;

namespace Velocity.Abstractions.State;

/// <summary>
/// Canonical address of one piece of mutable system state.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot and rollback engines are deliberately generic: they know how to read and write a
/// <see cref="StateKey"/> through a registered <see cref="IStateProvider"/>, and nothing else.
/// That means a new tweak gains crash safe rollback simply by declaring which keys it touches,
/// rather than hand writing an undo path that will eventually drift from the apply path.
/// </para>
/// <para>
/// The string form is <c>provider://path#item</c>, for example
/// <c>registry://HKLM/SYSTEM/CurrentControlSet/Control/PriorityControl#Win32PrioritySeparation</c>.
/// </para>
/// </remarks>
/// <param name="Provider">Identifier of the provider that can read and write the key.</param>
/// <param name="Path">Provider specific container path.</param>
/// <param name="Item">Optional item inside the container, such as a registry value name.</param>
public readonly record struct StateKey(string Provider, string Path, string? Item)
{
    /// <summary>Renders the canonical <c>provider://path#item</c> form.</summary>
    /// <returns>The canonical string form of the key.</returns>
    public override string ToString() =>
        Item is null ? $"{Provider}://{Path}" : $"{Provider}://{Path}#{Item}";

    /// <summary>Parses the canonical <c>provider://path#item</c> form.</summary>
    /// <param name="value">String produced by <see cref="ToString"/>.</param>
    /// <returns>The parsed key.</returns>
    /// <exception cref="FormatException">The value is not a canonical state key.</exception>
    public static StateKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new FormatException($"'{value}' is not a state key: missing provider scheme.");
        }

        string provider = value[..schemeEnd];
        string remainder = value[(schemeEnd + 3)..];
        int itemStart = remainder.IndexOf('#', StringComparison.Ordinal);

        return itemStart < 0
            ? new StateKey(provider, remainder, null)
            : new StateKey(provider, remainder[..itemStart], remainder[(itemStart + 1)..]);
    }
}
