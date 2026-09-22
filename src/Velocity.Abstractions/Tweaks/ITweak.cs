using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.State;

namespace Velocity.Abstractions.Tweaks;

/// <summary>
/// One optimization module.
/// </summary>
/// <remarks>
/// <para>
/// The engine, not the tweak, owns snapshotting and rollback. A tweak declares the state it will
/// touch through <see cref="GetStateKeys"/>; the transaction coordinator captures those keys
/// before <see cref="ApplyAsync"/> runs and can restore them afterwards without any tweak specific
/// undo code. Tweaks whose effect cannot be expressed as state keys additionally implement
/// <see cref="ICustomRollback"/>.
/// </para>
/// <para>
/// Implementations must be stateless and thread safe: one instance is registered in the container
/// and may be evaluated concurrently for detection while another transaction is running.
/// </para>
/// </remarks>
public interface ITweak
{
    /// <summary>Static metadata about this tweak.</summary>
    TweakDescriptor Descriptor { get; }

    /// <summary>
    /// The system state this tweak reads and writes on the given machine.
    /// </summary>
    /// <param name="context">Execution context describing the machine.</param>
    /// <returns>
    /// Every key that <see cref="ApplyAsync"/> may write. Returning a key that is not written is
    /// harmless; writing a key that was not returned fails the transaction.
    /// </returns>
    IReadOnlyList<StateKey> GetStateKeys(TweakContext context);

    /// <summary>Evaluates whether the tweak may run on this machine.</summary>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns>The compatibility result.</returns>
    Task<CompatibilityResult> CheckCompatibilityAsync(TweakContext context, CancellationToken cancellationToken);

    /// <summary>Reads the machine's current configuration for this tweak.</summary>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>What was observed.</returns>
    Task<TweakObservation> DetectAsync(TweakContext context, CancellationToken cancellationToken);

    /// <summary>Applies the change.</summary>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Token used to abort the apply.</param>
    /// <returns>What was done.</returns>
    Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken);

    /// <summary>Confirms that the change took effect by re-reading the machine.</summary>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Token used to abort the verification.</param>
    /// <returns>The verification result.</returns>
    Task<VerificationResult> VerifyAsync(TweakContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by tweaks whose effect cannot be restored by writing captured state keys back,
/// for example resuming processes that were suspended.
/// </summary>
public interface ICustomRollback
{
    /// <summary>
    /// Reverses the tweak's effect using tweak specific knowledge. Called by the rollback engine
    /// before generic state restoration, and must be safe to call when the apply never ran.
    /// </summary>
    /// <param name="context">Execution context.</param>
    /// <param name="rollbackPayload">
    /// Opaque data the tweak stored during apply through <see cref="TweakContext.SetRollbackPayload"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to abort the rollback.</param>
    /// <returns>A task that completes when the effect has been reversed.</returns>
    Task RollbackAsync(TweakContext context, string? rollbackPayload, CancellationToken cancellationToken);
}
