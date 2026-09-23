using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Games;

/// <summary>What to start.</summary>
/// <param name="ExecutablePath">Game executable, when one is known.</param>
/// <param name="LaunchCommand">
/// Store protocol URL, when the game is best started through its launcher.
/// </param>
/// <param name="Arguments">Command line arguments, for an executable launch.</param>
/// <param name="WorkingDirectory">Working directory, for an executable launch.</param>
public sealed record GameLaunchRequest(
    string? ExecutablePath = null,
    string? LaunchCommand = null,
    string? Arguments = null,
    string? WorkingDirectory = null);

/// <summary>Outcome of starting a game.</summary>
/// <param name="Started">Whether anything was started.</param>
/// <param name="ProcessId">
/// Identifier of the process that was started. For a store protocol launch this is the launcher, or
/// nothing at all, not the game.
/// </param>
/// <param name="IsGameProcess">
/// <see langword="true"/> only when <paramref name="ProcessId"/> is the game itself. When it is
/// false the caller must find the game by detection rather than by waiting on this handle.
/// </param>
/// <param name="Error">Why the launch failed, when it did.</param>
public sealed record GameLaunchResult(
    bool Started,
    int? ProcessId,
    bool IsGameProcess,
    string? Error = null);

/// <summary>Starts a game.</summary>
/// <remarks>
/// Launching through a store's protocol URL hands control to the launcher, which starts the game as
/// a separate process the caller never receives a handle to. That distinction is carried in
/// <see cref="GameLaunchResult.IsGameProcess"/> rather than hidden, because a session that waits on
/// the wrong handle reports a two second play time and restores the machine while the user is still
/// in the main menu.
/// </remarks>
public interface IGameLauncher
{
    /// <summary>Starts a game.</summary>
    /// <param name="request">What to start.</param>
    /// <param name="cancellationToken">Token used to abort the launch.</param>
    /// <returns>The result.</returns>
    Task<GameLaunchResult> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken);
}
