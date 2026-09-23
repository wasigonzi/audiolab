using System;

namespace Velocity.Abstractions.State;

/// <summary>Type of a captured state value.</summary>
public enum StateValueKind
{
    /// <summary>
    /// The item does not exist. Restoring an absent value deletes the item, which is what makes
    /// "the user never had this setting" reversible rather than being replaced by a guessed default.
    /// </summary>
    Absent = 0,

    /// <summary>UTF-16 string (registry REG_SZ).</summary>
    String = 1,

    /// <summary>Expandable string (registry REG_EXPAND_SZ).</summary>
    ExpandString = 2,

    /// <summary>Multi string (registry REG_MULTI_SZ), encoded as newline separated text.</summary>
    MultiString = 3,

    /// <summary>32 bit unsigned integer (registry REG_DWORD).</summary>
    UInt32 = 4,

    /// <summary>64 bit unsigned integer (registry REG_QWORD).</summary>
    UInt64 = 5,

    /// <summary>Opaque binary, base64 encoded in <see cref="StateValue.Data"/>.</summary>
    Binary = 6,

    /// <summary>Structured value serialised as JSON by the owning provider.</summary>
    Json = 7,
}

/// <summary>
/// A captured value together with enough type information to write it back unchanged.
/// </summary>
/// <param name="Kind">Type of the value.</param>
/// <param name="Data">
/// Canonical text form of the value, or <see langword="null"/> when <paramref name="Kind"/> is
/// <see cref="StateValueKind.Absent"/>.
/// </param>
public readonly record struct StateValue(StateValueKind Kind, string? Data)
{
    /// <summary>A value representing "this item does not exist".</summary>
    public static StateValue Absent => new(StateValueKind.Absent, null);

    /// <summary><see langword="true"/> when the item did not exist at capture time.</summary>
    public bool IsAbsent => Kind == StateValueKind.Absent;

    /// <summary>Creates a 32 bit integer value.</summary>
    /// <param name="value">Value to wrap.</param>
    /// <returns>The wrapped value.</returns>
    public static StateValue FromUInt32(uint value) =>
        new(StateValueKind.UInt32, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Creates a string value.</summary>
    /// <param name="value">Value to wrap.</param>
    /// <returns>The wrapped value.</returns>
    public static StateValue FromString(string value) => new(StateValueKind.String, value);

    /// <summary>Reads the value as a 32 bit unsigned integer.</summary>
    /// <returns>The numeric value, or <see langword="null"/> when the value is not numeric.</returns>
    public uint? AsUInt32() =>
        Kind is StateValueKind.UInt32 or StateValueKind.UInt64 &&
        uint.TryParse(Data, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : null;

    /// <summary>Renders a short human readable form for the UI and logs.</summary>
    /// <returns>A display string.</returns>
    public string ToDisplayString() => Kind switch
    {
        StateValueKind.Absent => "(not set)",
        StateValueKind.Binary => $"({Data?.Length ?? 0} base64 chars)",
        _ => Data ?? string.Empty,
    };
}
