using System;
using Velocity.Abstractions.State;

namespace Velocity.Core.State;

/// <summary>
/// Thrown when a tweak writes a state key it did not declare through
/// <see cref="Velocity.Abstractions.Tweaks.ITweak.GetStateKeysAsync"/>.
/// </summary>
/// <remarks>
/// This is treated as a defect in the tweak, not a runtime condition to recover from: an undeclared
/// write is a change that was never snapshotted and therefore cannot be rolled back. The
/// transaction fails and everything already applied is reversed.
/// </remarks>
public sealed class UndeclaredStateWriteException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="tweakId">Tweak that attempted the write.</param>
    /// <param name="key">Key that was not declared.</param>
    public UndeclaredStateWriteException(string tweakId, StateKey key)
        : base($"Tweak '{tweakId}' attempted to write '{key}', which it did not declare. " +
               "Undeclared writes cannot be rolled back, so the transaction has been failed.")
    {
        TweakId = tweakId;
        Key = key;
    }

    /// <summary>Tweak that attempted the write.</summary>
    public string TweakId { get; }

    /// <summary>Key that was not declared.</summary>
    public StateKey Key { get; }
}

/// <summary>Thrown when a write needs privileges the current process cannot obtain.</summary>
public sealed class PrivilegeRequiredException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">Key that requires elevation.</param>
    public PrivilegeRequiredException(StateKey key)
        : base($"Writing '{key}' requires the privileged helper, which is not available.") =>
        Key = key;

    /// <summary>Key that requires elevation.</summary>
    public StateKey Key { get; }
}
