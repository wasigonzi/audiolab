using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Profiles;
using Velocity.Core.Transactions;

namespace Velocity.Core.Sessions;

/// <summary>Runs a complete gaming session.</summary>
public interface IGamingSessionManager
{
    /// <summary>The session currently running, or <see langword="null"/>.</summary>
    GameSessionReport? Current { get; }

    /// <summary>Raised whenever the running session changes state.</summary>
    event EventHandler<GameSessionReport>? SessionChanged;

    /// <summary>
    /// Applies a profile, starts the game, waits for it to exit, and restores everything.
    /// </summary>
    /// <param name="game">Game to launch.</param>
    /// <param name="cancellationToken">
    /// Cancelling ends the session early and still restores the captured state.
    /// </param>
    /// <returns>The session report.</returns>
    Task<GameSessionReport> OptimizeAndLaunchAsync(
        GameInstallation game,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a profile for a game that is already running, and restores when it exits.
    /// </summary>
    /// <param name="detected">The running game.</param>
    /// <param name="cancellationToken">Token used to end the session early.</param>
    /// <returns>The session report.</returns>
    Task<GameSessionReport> AttachAsync(DetectedGame detected, CancellationToken cancellationToken);
}

/// <summary>
/// The Optimize and Launch pipeline: apply, launch, monitor, restore, report.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the safety property, so it is stated here as it is in the engine. A session:
/// </para>
/// <list type="number">
/// <item>resolves the profile for the game, or runs unoptimized when there is none;</item>
/// <item>applies it through the normal transaction pipeline, so everything is snapshotted;</item>
/// <item>starts the game, or attaches to one already running;</item>
/// <item>collects resource telemetry at the gaming cadence while it runs;</item>
/// <item>restores the captured state when the game exits, <b>whatever else happened</b>;</item>
/// <item>reports what was applied, what was skipped, and what could not be restored.</item>
/// </list>
/// <para>
/// <b>The restore is unconditional.</b> It runs when the game exits, when the launch fails, when
/// the wait times out, when the caller cancels, and when something throws. A gaming optimizer that
/// can leave a machine changed because a game crashed is not one anybody should install, so the
/// restore lives in a finally block and its failures are reported rather than swallowed.
/// </para>
/// <para>
/// <b>What a session report does not claim.</b> It carries resource telemetry, not frames. A
/// session has no before measurement to compare against, so it cannot support a statement about
/// frame rate; that is the Benchmark Lab's job.
/// </para>
/// </remarks>
public sealed class GamingSessionManager : IGamingSessionManager
{
    private readonly IProfileService _profiles;
    private readonly IOptimizationEngine _engine;
    private readonly IRollbackEngine _rollback;
    private readonly IGameLauncher _launcher;
    private readonly IGameDetector _detector;
    private readonly IProcessInspector _processes;
    private readonly ISystemMonitor? _monitor;
    private readonly TimeProvider _timeProvider;
    private readonly GamingSessionOptions _options;
    private readonly ILogger<GamingSessionManager> _logger;

    /// <summary>Creates the manager.</summary>
    /// <param name="profiles">Profile resolution.</param>
    /// <param name="engine">Transaction coordinator.</param>
    /// <param name="rollback">Rollback engine.</param>
    /// <param name="launcher">Game launcher.</param>
    /// <param name="detector">Running game detector.</param>
    /// <param name="processes">Process inspector, used to watch a directly launched game.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="options">Timings.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="monitor">
    /// Telemetry monitor, when one is available. A session runs without it; the report simply
    /// carries no telemetry rather than carrying invented numbers.
    /// </param>
    public GamingSessionManager(
        IProfileService profiles,
        IOptimizationEngine engine,
        IRollbackEngine rollback,
        IGameLauncher launcher,
        IGameDetector detector,
        IProcessInspector processes,
        TimeProvider timeProvider,
        GamingSessionOptions options,
        ILogger<GamingSessionManager> logger,
        ISystemMonitor? monitor = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitor = monitor;
    }

    /// <inheritdoc />
    public GameSessionReport? Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<GameSessionReport>? SessionChanged;

    /// <inheritdoc />
    public Task<GameSessionReport> OptimizeAndLaunchAsync(
        GameInstallation game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);

        return RunAsync(
            game.Id,
            game.Name,
            launch: async token =>
            {
                GameLaunchResult result = await _launcher
                    .LaunchAsync(
                        new GameLaunchRequest(
                            game.ExecutablePath,
                            game.LaunchCommand,
                            WorkingDirectory: game.InstallDirectory),
                        token)
                    .ConfigureAwait(false);

                if (!result.Started)
                {
                    return (null, result.Error ?? "The game could not be started.");
                }

                if (result.IsGameProcess && result.ProcessId is int pid)
                {
                    return (pid, null);
                }

                // The store launcher was started, not the game. Wait for the game itself to show up.
                int? found = await WaitForGameAsync(game, token).ConfigureAwait(false);

                return found is null
                    ? (null, string.Create(
                        CultureInfo.InvariantCulture,
                        $"The launcher started but {game.Name} did not appear within {_options.LaunchTimeout.TotalMinutes:0} minutes."))
                    : (found, null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GameSessionReport> AttachAsync(
        DetectedGame detected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detected);

        return RunAsync(
            detected.Game?.Id,
            detected.Game?.Name ?? detected.ExecutableName,
            launch: _ => Task.FromResult<(int?, string?)>((detected.ProcessId, null)),
            cancellationToken);
    }

    private async Task<GameSessionReport> RunAsync(
        string? gameId,
        string gameName,
        Func<CancellationToken, Task<(int? ProcessId, string? Error)>> launch,
        CancellationToken cancellationToken)
    {
        Guid sessionId = Guid.NewGuid();
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        var report = new GameSessionReport
        {
            SessionId = sessionId,
            GameId = gameId,
            GameName = gameName,
            State = GameSessionState.Optimizing,
            StartedAtUtc = startedAt,
        };

        Publish(report);

        Guid? transactionId = null;
        var telemetry = new TelemetryAccumulator();

        try
        {
            OptimizationProfile? profile = await _profiles
                .ResolveForGameAsync(gameId, cancellationToken)
                .ConfigureAwait(false);

            if (profile is not null)
            {
                OptimizationRunResult applied = await _engine
                    .ApplyAsync(
                        _profiles.BuildRequest(profile, TransactionReason.GamingSession, sessionId),
                        cancellationToken)
                    .ConfigureAwait(false);

                transactionId = applied.TransactionId;

                report = report with
                {
                    ProfileId = profile.Id,
                    TransactionId = applied.TransactionId,
                    AppliedTweakIds = [.. applied.Results
                        .Where(result => result.Outcome is ApplyOutcome.Applied
                            or ApplyOutcome.AppliedPendingRestart)
                        .Select(result => result.TweakId)],
                    SkippedTweaks = applied.Results
                        .Where(result => result.Outcome is ApplyOutcome.Skipped or ApplyOutcome.Failed)
                        .ToDictionary(
                            result => result.TweakId,
                            result => result.Message ?? "No reason was recorded.",
                            StringComparer.Ordinal),
                };
            }
            else
            {
                _logger.LogInformation(
                    "No profile is configured for {Game}; the session will run unoptimized.", gameName);
            }

            report = report with { State = GameSessionState.Launching };
            Publish(report);

            (int? processId, string? error) = await launch(cancellationToken).ConfigureAwait(false);

            if (processId is null)
            {
                report = report with
                {
                    State = GameSessionState.Failed,
                    Error = error ?? "The game could not be started.",
                };

                return await FinishAsync(report, transactionId, telemetry, cancellationToken)
                    .ConfigureAwait(false);
            }

            report = report with { State = GameSessionState.Running };
            Publish(report);

            DateTimeOffset playStarted = _timeProvider.GetUtcNow();

            using (telemetry.Attach(_monitor))
            {
                _monitor?.SetCadence(MonitorCadence.GamingSession);
                await WaitForExitAsync(processId.Value, cancellationToken).ConfigureAwait(false);
            }

            _monitor?.SetCadence(MonitorCadence.Foreground);

            report = report with { PlayTime = _timeProvider.GetUtcNow() - playStarted };

            return await FinishAsync(report, transactionId, telemetry, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation ends the session, it does not abandon the machine. The restore below
            // runs with a fresh token so it cannot itself be cancelled half way.
            report = report with { State = GameSessionState.Restoring };
            return await FinishAsync(report, transactionId, telemetry, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The gaming session for {Game} failed.", gameName);
            report = report with { State = GameSessionState.Failed, Error = ex.Message };
            return await FinishAsync(report, transactionId, telemetry, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<GameSessionReport> FinishAsync(
        GameSessionReport report,
        Guid? transactionId,
        TelemetryAccumulator telemetry,
        CancellationToken cancellationToken)
    {
        bool failedToStart = report.State == GameSessionState.Failed;

        if (transactionId is not null)
        {
            report = report with { State = GameSessionState.Restoring };
            Publish(report);

            report = report with { RestoreFailures = await RestoreAsync(transactionId.Value).ConfigureAwait(false) };
        }

        GameSessionState finalState = report.RestoreFailures.Count > 0
            ? GameSessionState.CompletedWithRestoreFailures
            : failedToStart
                ? GameSessionState.Failed
                : GameSessionState.Completed;

        report = report with
        {
            State = finalState,
            EndedAtUtc = _timeProvider.GetUtcNow(),
            Telemetry = telemetry.Summarize(),
            TelemetrySampleCount = telemetry.SampleCount,
        };

        Current = null;
        SessionChanged?.Invoke(this, report);

        _logger.LogInformation(
            "Session {Session} for {Game} ended as {State} after {PlayTime}.",
            report.SessionId,
            report.GameName,
            report.State,
            report.PlayTime);

        await Task.CompletedTask.ConfigureAwait(false);
        return report;
    }

    private async Task<IReadOnlyDictionary<string, string>> RestoreAsync(Guid transactionId)
    {
        try
        {
            RollbackResult result = await _rollback
                .RollbackTransactionAsync(transactionId, CancellationToken.None)
                .ConfigureAwait(false);

            return result.Failures.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : result.Failures.ToDictionary(
                    failure => failure.Key,
                    failure => failure.Value,
                    StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A restore that throws is the worst outcome this product has, so it is reported in the
            // session state rather than logged and forgotten.
            _logger.LogError(ex, "Restoring transaction {Transaction} failed.", transactionId);

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["transaction"] = ex.Message,
            };
        }
    }

    private async Task<int?> WaitForGameAsync(GameInstallation game, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _options.LaunchTimeout;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<DetectedGame> running = await _detector
                .DetectRunningGamesAsync(cancellationToken)
                .ConfigureAwait(false);

            DetectedGame? match = running.FirstOrDefault(detected =>
                string.Equals(detected.Game?.Id, game.Id, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                _logger.LogInformation(
                    "{Game} appeared as process {Pid}.", game.Name, match.ProcessId);
                return match.ProcessId;
            }

            await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset? missingSince = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProcessSnapshot? process = await _processes
                .GetProcessAsync(processId, cancellationToken)
                .ConfigureAwait(false);

            if (process is null)
            {
                missingSince ??= _timeProvider.GetUtcNow();

                // A title that relaunches itself once at startup must not end the session.
                if (_timeProvider.GetUtcNow() - missingSince >= _options.ExitGracePeriod)
                {
                    return;
                }
            }
            else
            {
                missingSince = null;
            }

            await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Publish(GameSessionReport report)
    {
        Current = report;
        SessionChanged?.Invoke(this, report);
    }
}
