using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;

namespace Velocity.Core.Games;

/// <summary>
/// Reads the Epic Games Store library from the launcher's manifest folder.
/// </summary>
/// <remarks>
/// <para>
/// Epic writes one JSON <c>.item</c> file per installed game under
/// <c>ProgramData\Epic\EpicGamesLauncher\Data\Manifests</c>. Unlike Steam, these manifests name the
/// launch executable, so per-game settings can address the real process rather than a guess.
/// </para>
/// <para>
/// A manifest that fails to parse is skipped and logged. Epic adds fields between launcher
/// versions, so the reader takes the handful it needs and ignores everything else rather than
/// binding to a shape that will change.
/// </para>
/// </remarks>
public sealed class EpicLibrarySource : IGameLibrarySource
{
    private readonly IStoreLocator _locator;
    private readonly IGameFileSystem _fileSystem;
    private readonly ILogger<EpicLibrarySource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="locator">Locates the Epic manifest folder.</param>
    /// <param name="fileSystem">File access.</param>
    /// <param name="logger">Logger.</param>
    public EpicLibrarySource(
        IStoreLocator locator,
        IGameFileSystem fileSystem,
        ILogger<EpicLibrarySource> logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public GameStore Store => GameStore.Epic;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameInstallation>> ScanAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = await _locator
            .GetStoreRootsAsync(GameStore.Epic, cancellationToken)
            .ConfigureAwait(false);

        var games = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (!_fileSystem.DirectoryExists(root))
            {
                continue;
            }

            foreach (string file in _fileSystem.EnumerateFiles(root, "*.item"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? text = await _fileSystem
                    .ReadAllTextAsync(file, cancellationToken)
                    .ConfigureAwait(false);

                GameInstallation? game = ParseManifest(text, _fileSystem);

                if (game is null)
                {
                    _logger.LogDebug("Skipping Epic manifest {File}: it could not be read.", file);
                    continue;
                }

                games[game.Id] = game;
            }
        }

        return [.. games.Values];
    }

    /// <summary>Builds an installation from one Epic <c>.item</c> manifest.</summary>
    /// <param name="json">Manifest text.</param>
    /// <param name="fileSystem">File access, used to build the executable path.</param>
    /// <returns>The installation, or <see langword="null"/> when the manifest is unusable.</returns>
    public static GameInstallation? ParseManifest(string? json, IGameFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? appName = ReadString(root, "AppName");
            string? displayName = ReadString(root, "DisplayName");

            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            string? installLocation = ReadString(root, "InstallLocation");
            string? launchExecutable = ReadString(root, "LaunchExecutable");

            string? executablePath =
                string.IsNullOrWhiteSpace(installLocation) || string.IsNullOrWhiteSpace(launchExecutable)
                    ? null
                    : fileSystem.Combine(installLocation, launchExecutable.Replace('/', '\\'));

            long? size = root.TryGetProperty("InstallSize", out JsonElement sizeElement) &&
                         sizeElement.TryGetInt64(out long parsed)
                ? parsed
                : null;

            string? namespaceId = ReadString(root, "CatalogNamespace");
            string? itemId = ReadString(root, "CatalogItemId");

            return new GameInstallation
            {
                Id = GameIdentity.ForStoreApp(GameStore.Epic, appName),
                Name = displayName,
                Store = GameStore.Epic,
                StoreAppId = appName,
                InstallDirectory = installLocation,
                ExecutablePath = executablePath,
                LaunchCommand = BuildLaunchCommand(namespaceId, itemId, appName),
                SizeBytes = size,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? BuildLaunchCommand(string? namespaceId, string? itemId, string appName)
    {
        // The launcher protocol needs all three parts; without them the caller falls back to the
        // executable rather than sending a URL that silently does nothing.
        if (string.IsNullOrWhiteSpace(namespaceId) || string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        return $"com.epicgames.launcher://apps/{namespaceId}%3A{itemId}%3A{appName}?action=launch&silent=true";
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
