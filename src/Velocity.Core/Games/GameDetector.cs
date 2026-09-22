using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;
using Velocity.Abstractions.Processes;

namespace Velocity.Core.Games;

/// <summary>
/// Decides which running processes are games.
/// </summary>
/// <remarks>
/// <para>
/// Detection is evidence based and says which evidence it used. A process is a game because its
/// image is the executable named in an installed game's manifest, because it lives inside a game's
/// install directory, or because the user said so. That is the whole list.
/// </para>
/// <para>
/// <b>What this deliberately does not do.</b> It does not guess from GPU usage, window style or
/// process name patterns. Those heuristics identify a video call, a browser playing video and a
/// 3D modelling tool as games, and acting on that guess means applying gaming optimizations to the
/// wrong process and attributing a benchmark to the wrong workload. Windows exposes no documented
/// API that answers "is this process a game", so where the evidence runs out this reports nothing
/// rather than inventing an answer.
/// </para>
/// <para>
/// Protected processes are never reported, whatever their path. A security agent living under a
/// game's install directory is still a security agent.
/// </para>
/// </remarks>
public sealed class GameDetector : IGameDetector
{
    private readonly IProcessInspector _processes;
    private readonly IGameLibrary _library;
    private readonly IUserDeclaredGames _userDeclared;
    private readonly ILogger<GameDetector> _logger;

    /// <summary>Creates the detector.</summary>
    /// <param name="processes">Process inspector.</param>
    /// <param name="library">Installed game library.</param>
    /// <param name="userDeclared">Executables the user has marked as games.</param>
    /// <param name="logger">Logger.</param>
    public GameDetector(
        IProcessInspector processes,
        IGameLibrary library,
        IUserDeclaredGames userDeclared,
        ILogger<GameDetector> logger)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _userDeclared = userDeclared ?? throw new ArgumentNullException(nameof(userDeclared));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DetectedGame>> DetectRunningGamesAsync(
        CancellationToken cancellationToken)
    {
        GameLibraryScan library = await _library.GetAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProcessSnapshot> running =
            await _processes.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> declared =
            await _userDeclared.GetDeclaredExecutablesAsync(cancellationToken).ConfigureAwait(false);

        var detected = new List<DetectedGame>();

        foreach (ProcessSnapshot process in running)
        {
            if (process.Protection == ProcessProtection.Protected)
            {
                continue;
            }

            DetectedGame? match = Match(process, library.Games, declared);

            if (match is not null)
            {
                detected.Add(match);
            }
        }

        if (detected.Count > 0)
        {
            _logger.LogDebug(
                "Detected {Count} running game(s): {Names}.",
                detected.Count,
                string.Join(", ", detected.Select(game => game.ExecutableName)));
        }

        // Strongest evidence first, then the largest process, which for equal evidence is the one a
        // user would point at.
        return
        [
            .. detected
                .OrderBy(game => (int)game.Reason)
                .ThenByDescending(game => game.Game is not null)
                .ThenBy(game => game.ExecutableName, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Matches one process against the library.</summary>
    /// <param name="process">Process to classify.</param>
    /// <param name="games">Installed games.</param>
    /// <param name="declaredExecutables">Executable names the user marked as games.</param>
    /// <returns>The detection, or <see langword="null"/> when the process is not a game.</returns>
    public static DetectedGame? Match(
        ProcessSnapshot process,
        IReadOnlyList<GameInstallation> games,
        IReadOnlySet<string> declaredExecutables)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(declaredExecutables);

        if (process.Protection == ProcessProtection.Protected)
        {
            return null;
        }

        // An exact executable match is the strongest evidence there is.
        foreach (GameInstallation game in games)
        {
            if (game.ExecutablePath is not null &&
                process.ExecutablePath is not null &&
                string.Equals(game.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return new DetectedGame(
                    process.ProcessId,
                    process.ExecutableName,
                    process.ExecutablePath,
                    game,
                    GameDetectionReason.InstalledLibraryMatch);
            }
        }

        if (declaredExecutables.Contains(process.ExecutableName))
        {
            return new DetectedGame(
                process.ProcessId,
                process.ExecutableName,
                process.ExecutablePath,
                games.FirstOrDefault(game => GameIdentity.IsUnder(process.ExecutablePath, game.InstallDirectory)),
                GameDetectionReason.UserDeclared);
        }

        // Living inside a game's install directory is weaker: it also matches the title's launcher
        // and its crash reporter, so the reason travels with the result and the UI says so.
        foreach (GameInstallation game in games)
        {
            if (GameIdentity.IsUnder(process.ExecutablePath, game.InstallDirectory))
            {
                return new DetectedGame(
                    process.ProcessId,
                    process.ExecutableName,
                    process.ExecutablePath,
                    game,
                    GameDetectionReason.StoreLibraryFolder);
            }
        }

        return null;
    }
}

/// <summary>Executables the user has marked as games by hand.</summary>
/// <remarks>
/// This exists because detection is deliberately conservative. A user running something the
/// scanners cannot see — a title from a store with no manifest, a private build, an emulator —
/// needs a way to say so, and saying so explicitly is better than the product guessing.
/// </remarks>
public interface IUserDeclaredGames
{
    /// <summary>Executable file names the user marked as games.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The names, compared case insensitively.</returns>
    Task<IReadOnlySet<string>> GetDeclaredExecutablesAsync(CancellationToken cancellationToken);

    /// <summary>Marks an executable as a game, or removes the mark.</summary>
    /// <param name="executableName">Executable file name, without a path.</param>
    /// <param name="isGame">Whether it should be treated as a game.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the change is stored.</returns>
    Task DeclareAsync(string executableName, bool isGame, CancellationToken cancellationToken);
}
