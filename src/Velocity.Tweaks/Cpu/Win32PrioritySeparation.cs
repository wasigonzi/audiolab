using System;
using System.Globalization;

namespace Velocity.Tweaks.Cpu;

/// <summary>Length of a thread's scheduling quantum.</summary>
public enum QuantumLength
{
    /// <summary>Whatever the edition of Windows defaults to: short on client, long on server.</summary>
    OperatingSystemDefault = 0,

    /// <summary>Short quantums. The client default.</summary>
    Short = 1,

    /// <summary>Long quantums. The server default.</summary>
    Long = 2,
}

/// <summary>Whether a thread's quantum is stretched when it is in the foreground.</summary>
public enum QuantumType
{
    /// <summary>Whatever the edition of Windows defaults to: variable on client.</summary>
    OperatingSystemDefault = 0,

    /// <summary>The foreground process gets a longer quantum than background processes.</summary>
    Variable = 1,

    /// <summary>Every thread gets the same quantum regardless of foreground state.</summary>
    Fixed = 2,
}

/// <summary>How much the foreground process's quantum is multiplied by.</summary>
public enum ForegroundBoost
{
    /// <summary>No additional foreground quantum.</summary>
    None = 0,

    /// <summary>The foreground quantum is twice the background quantum.</summary>
    Double = 1,

    /// <summary>The foreground quantum is three times the background quantum. The client default.</summary>
    Triple = 2,
}

/// <summary>
/// The decoded form of <c>Win32PrioritySeparation</c>.
/// </summary>
/// <remarks>
/// <para>
/// The value is six meaningful bits, documented in Windows Internals and unchanged since Windows
/// 2000:
/// </para>
/// <list type="bullet">
/// <item>bits 0-1: foreground boost (0 none, 1 double, 2 triple)</item>
/// <item>bits 2-3: quantum type (0 OS default, 1 variable, 2 fixed)</item>
/// <item>bits 4-5: quantum length (0 OS default, 1 short, 2 long)</item>
/// </list>
/// <para>
/// A client installation of Windows defaults to decimal 2, which is short, variable quantums with
/// the maximum foreground boost already in effect. That default is the reason this module's
/// description does not promise an improvement: the only thing left to change is variable versus
/// fixed quantums, and which is better depends on the game.
/// </para>
/// </remarks>
/// <param name="Boost">Foreground quantum multiplier.</param>
/// <param name="Type">Whether the foreground quantum is stretched.</param>
/// <param name="Length">Base quantum length.</param>
public readonly record struct PrioritySeparation(
    ForegroundBoost Boost,
    QuantumType Type,
    QuantumLength Length)
{
    /// <summary>The Windows client default: short, variable, triple foreground boost (decimal 2).</summary>
    public static PrioritySeparation WindowsClientDefault { get; } =
        new(ForegroundBoost.Triple, QuantumType.OperatingSystemDefault, QuantumLength.OperatingSystemDefault);

    /// <summary>
    /// Short, fixed quantums with the maximum foreground boost (decimal 26).
    /// </summary>
    /// <remarks>
    /// Fixed quantums mean a background thread is not cut short relative to the foreground one.
    /// For a game that keeps worker threads busy outside the foreground window's thread, that can
    /// reduce scheduling jitter; for a game that does not, it does nothing. It is a hypothesis to
    /// measure, not a setting to assume.
    /// </remarks>
    public static PrioritySeparation ShortFixedMaximumBoost { get; } =
        new(ForegroundBoost.Triple, QuantumType.Fixed, QuantumLength.Short);

    /// <summary>Decodes a raw registry value.</summary>
    /// <param name="value">Raw <c>Win32PrioritySeparation</c> value.</param>
    /// <returns>The decoded form.</returns>
    public static PrioritySeparation FromRaw(uint value) => new(
        (ForegroundBoost)Math.Min(value & 0b11u, 2u),
        (QuantumType)Math.Min((value >> 2) & 0b11u, 2u),
        (QuantumLength)Math.Min((value >> 4) & 0b11u, 2u));

    /// <summary>Encodes back to the raw registry value.</summary>
    /// <returns>The raw value.</returns>
    public uint ToRaw() => (uint)Boost | ((uint)Type << 2) | ((uint)Length << 4);

    /// <summary>Describes the configuration in the words a user would use.</summary>
    /// <returns>A display string.</returns>
    public override string ToString()
    {
        string length = Length switch
        {
            QuantumLength.Short => "short",
            QuantumLength.Long => "long",
            _ => "default-length",
        };

        string type = Type switch
        {
            QuantumType.Variable => "variable",
            QuantumType.Fixed => "fixed",
            _ => "default-type",
        };

        string boost = Boost switch
        {
            ForegroundBoost.None => "no foreground boost",
            ForegroundBoost.Double => "2x foreground boost",
            _ => "3x foreground boost",
        };

        return string.Create(
            CultureInfo.InvariantCulture, $"{length}, {type} quantums, {boost} (raw {ToRaw()})");
    }
}
