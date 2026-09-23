using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;

namespace Velocity.Platform.Windows.Games;

/// <summary>
/// Starts a game, preferring the store's own launch command over the executable.
/// </summary>
/// <remarks>
/// <para>
/// A store protocol URL is preferred because it is what the user's own shortcut does: the launcher
/// sets up the overlay, the DRM wrapper and the cloud save sync that many titles will not start
/// without. Running the executable directly is the fallback for a game with no launch command.
/// </para>
/// <para>
/// <b>The game is never started elevated.</b> This process is not elevated to begin with, and the
/// launch uses the shell exactly as Explorer would. A game running as administrator because an
/// optimizer started it is a security regression the user did not ask for.
/// </para>
/// <para>
/// When the launch goes through the store, the returned process is the launcher, and
/// <see cref="GameLaunchResult.IsGameProcess"/> says so. The caller then finds the game by
/// detection rather than waiting on a handle that belongs to something else.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameLauncher : IGameLauncher
{
    private readonly ILogger<WindowsGameLauncher> _logger;

    /// <summary>Creates the launcher.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsGameLauncher(ILogger<WindowsGameLauncher> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(request.LaunchCommand))
        {
            return Task.FromResult(StartThroughStore(request.LaunchCommand));
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return Task.FromResult(StartExecutable(request));
        }

        return Task.FromResult(new GameLaunchResult(
            Started: false,
            ProcessId: null,
            IsGameProcess: false,
            Error: "This game has neither a launch command nor a known executable, so it cannot be " +
                   "started from here. Start it from its launcher and this product will attach to it."));
    }

    private GameLaunchResult StartThroughStore(string launchCommand)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = launchCommand,
                UseShellExecute = true,
            });

            _logger.LogInformation("Asked the store launcher to start {Command}.", launchCommand);

            // Whatever the shell returns here is the launcher or nothing at all, never the game.
            return new GameLaunchResult(
                Started: true,
                ProcessId: started?.Id,
                IsGameProcess: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException or System.IO.FileNotFoundException)
        {
            _logger.LogWarning(ex, "Could not start {Command}.", launchCommand);
            return new GameLaunchResult(false, null, false, ex.Message);
        }
    }

    private GameLaunchResult StartExecutable(GameLaunchRequest request)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.ExecutablePath!,
                UseShellExecute = true,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(request.Arguments))
            {
                startInfo.Arguments = request.Arguments;
            }

            using Process? started = Process.Start(startInfo);

            if (started is null)
            {
                return new GameLaunchResult(
                    false, null, false, "Windows did not return a process for the game.");
            }

            _logger.LogInformation(
                "Started {Path} as process {Pid}.", request.ExecutablePath, started.Id);

            return new GameLaunchResult(true, started.Id, IsGameProcess: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException or System.IO.FileNotFoundException)
        {
            _logger.LogWarning(ex, "Could not start {Path}.", request.ExecutablePath);
            return new GameLaunchResult(false, null, false, ex.Message);
        }
    }
}
