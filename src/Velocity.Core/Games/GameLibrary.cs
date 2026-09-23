using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;

namespace Velocity.Core.Games;

/// <summary>
/// Builds the machine's game library by asking every registered source.
/// </summary>
/// <remarks>
/// <para>
/// A source that throws does not fail the scan: the other stores' games are still returned and the
/// failure is recorded in <see cref="GameLibraryScan.FailedSources"/> so the UI can say "Epic could
/// not be read" instead of silently showing an incomplete library, which a user would read as "my
/// Epic games are not supported".
/// </para>
/// <para>
/// The result is cached until the next explicit scan. Re-reading every manifest on each query would
/// cost disk I/O during a game, which is the one moment this product must not add any.
/// </para>
/// </remarks>
public sealed class GameLibrary : IGameLibrary
{
    private readonly IReadOnlyList<IGameLibrarySource> _sources;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameLibrary> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GameLibraryScan? _cached;

    /// <summary>Creates the library.</summary>
    /// <param name="sources">Sources to read.</param>
    /// <param name="timeProvider">Clock used to stamp the scan.</param>
    /// <param name="logger">Logger.</param>
    public GameLibrary(
        IEnumerable<IGameLibrarySource> sources,
        TimeProvider timeProvider,
        ILogger<GameLibrary> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = [.. sources];
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GameLibraryScan> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            var games = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);
            var failures = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (IGameLibrarySource source in _sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    IReadOnlyList<GameInstallation> found =
                        await source.ScanAsync(cancellationToken).ConfigureAwait(false);

                    foreach (GameInstallation game in found)
                    {
                        games[game.Id] = game with { LastSeenUtc = now };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The {Store} library could not be read.", source.Store);
                    failures[source.Store.ToString()] = ex.Message;
                }
            }

            _cached = new GameLibraryScan(
                [.. games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)],
                failures);

            _logger.LogInformation(
                "Game library scan found {Count} games across {Sources} sources ({Failures} failed).",
                _cached.Games.Count,
                _sources.Count,
                failures.Count);

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameLibraryScan> GetAsync(CancellationToken cancellationToken)
    {
        GameLibraryScan? cached = _cached;
        return cached ?? await ScanAsync(cancellationToken).ConfigureAwait(false);
    }
}
