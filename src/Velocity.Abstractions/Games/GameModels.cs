using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Games;

/// <summary>Where a game was installed from.</summary>
public enum GameStore
{
    /// <summary>Found on disk or running, with no store attribution.</summary>
    Unknown = 0,

    /// <summary>Steam.</summary>
    Steam = 1,

    /// <summary>Epic Games Store.</summary>
    Epic = 2,

    /// <summary>GOG Galaxy.</summary>
    Gog = 3,

    /// <summary>EA App / Origin.</summary>
    Ea = 4,

    /// <summary>Ubisoft Connect.</summary>
    Ubisoft = 5,

    /// <summary>Battle.net.</summary>
    BattleNet = 6,

    /// <summary>Xbox / Microsoft Store.</summary>
    Xbox = 7,

    /// <summary>Added by the user by hand.</summary>
    Manual = 8,
}

/// <summary>How a running process was decided to be a game.</summary>
/// <remarks>
/// The reason travels with the detection so the UI can be honest about it. "I recognised this
/// executable" and "something is using the GPU heavily" are not the same claim, and presenting the
/// second as the first is how a product ends up optimizing for a video call.
/// </remarks>
public enum GameDetectionReason
{
    /// <summary>Not detected as a game.</summary>
    None = 0,

    /// <summary>The executable path matches a game in the installed library.</summary>
    InstalledLibraryMatch = 1,

    /// <summary>The executable sits inside a known store's library folder.</summary>
    StoreLibraryFolder = 2,

    /// <summary>The user marked this executable as a game.</summary>
    UserDeclared = 3,

    /// <summary>The application asked Windows for the gaming quality of service.</summary>
    DeclaredByProcess = 4,
}

/// <summary>A game installed on this machine.</summary>
public sealed record GameInstallation
{
    /// <summary>
    /// Stable identifier. For a store title this is <c>store:appid</c>, for a manually added game
    /// it is derived from the executable path, so the same game keeps its profile across scans.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Store the game came from.</summary>
    public GameStore Store { get; init; } = GameStore.Unknown;

    /// <summary>Store specific application identifier, when there is one.</summary>
    public string? StoreAppId { get; init; }

    /// <summary>Directory the game is installed in.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>
    /// Main executable, when it is known. A store manifest does not always name one, and guessing
    /// the largest .exe in the folder picks the anti-cheat service as often as the game.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Command used to launch the game, which for a store title is a protocol URL.</summary>
    public string? LaunchCommand { get; init; }

    /// <summary>Installation size in bytes, when the manifest reports it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>When this entry was last seen by a scan.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>A running process identified as a game.</summary>
/// <param name="ProcessId">Process identifier.</param>
/// <param name="ExecutableName">Executable file name.</param>
/// <param name="ExecutablePath">Full image path, when readable.</param>
/// <param name="Game">The matching installation, when one was found.</param>
/// <param name="Reason">Why this process was considered a game.</param>
public sealed record DetectedGame(
    int ProcessId,
    string ExecutableName,
    string? ExecutablePath,
    GameInstallation? Game,
    GameDetectionReason Reason);

/// <summary>Finds games installed by one store.</summary>
public interface IGameLibrarySource
{
    /// <summary>Store this source reads.</summary>
    GameStore Store { get; }

    /// <summary>Enumerates the games this store has installed.</summary>
    /// <param name="cancellationToken">Token used to abort the scan.</param>
    /// <returns>
    /// The installations found. An empty list means the store is not installed or its library is
    /// empty; a source reports a failure by throwing, so the scanner can record it.
    /// </returns>
    Task<IReadOnlyList<GameInstallation>> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>Result of scanning every library source.</summary>
/// <param name="Games">Games found, de-duplicated by identifier.</param>
/// <param name="FailedSources">Sources that threw, with the reason, so the UI can say so.</param>
public sealed record GameLibraryScan(
    IReadOnlyList<GameInstallation> Games,
    IReadOnlyDictionary<string, string> FailedSources);

/// <summary>Builds the machine's game library from every available source.</summary>
public interface IGameLibrary
{
    /// <summary>Re-scans every source.</summary>
    /// <param name="cancellationToken">Token used to abort the scan.</param>
    /// <returns>The scan result.</returns>
    Task<GameLibraryScan> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Returns the last scan, scanning once if none has been performed.</summary>
    /// <param name="cancellationToken">Token used to abort the scan.</param>
    /// <returns>The scan result.</returns>
    Task<GameLibraryScan> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Decides which running processes are games.</summary>
public interface IGameDetector
{
    /// <summary>Finds the games currently running.</summary>
    /// <param name="cancellationToken">Token used to abort the scan.</param>
    /// <returns>The detected games, most likely first.</returns>
    Task<IReadOnlyList<DetectedGame>> DetectRunningGamesAsync(CancellationToken cancellationToken);
}
