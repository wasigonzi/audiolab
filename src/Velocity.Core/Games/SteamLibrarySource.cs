using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;

namespace Velocity.Core.Games;

/// <summary>
/// Reads the Steam library from <c>libraryfolders.vdf</c> and the app manifests beside it.
/// </summary>
/// <remarks>
/// <para>
/// Steam keeps one manifest per installed game in <c>steamapps/appmanifest_&lt;appid&gt;.acf</c>,
/// and the set of library folders in <c>steamapps/libraryfolders.vdf</c>. Both are the documented,
/// stable on-disk format; nothing here talks to Steam's process or its web API.
/// </para>
/// <para>
/// <b>The executable is usually not known.</b> A Steam manifest names the install directory, not
/// the game binary. Guessing — taking the largest executable, or the one matching the folder name —
/// picks the anti-cheat service or a crash handler often enough to matter, and a wrong executable
/// means per-game settings applied to the wrong process. So the executable is left null and the
/// game is launched through <c>steam://rungameid/</c>, which is what the user's own shortcut does.
/// </para>
/// </remarks>
public sealed class SteamLibrarySource : IGameLibrarySource
{
    private readonly IStoreLocator _locator;
    private readonly IGameFileSystem _fileSystem;
    private readonly ILogger<SteamLibrarySource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="locator">Locates the Steam installation.</param>
    /// <param name="fileSystem">File access.</param>
    /// <param name="logger">Logger.</param>
    public SteamLibrarySource(
        IStoreLocator locator,
        IGameFileSystem fileSystem,
        ILogger<SteamLibrarySource> logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public GameStore Store => GameStore.Steam;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameInstallation>> ScanAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = await _locator
            .GetStoreRootsAsync(GameStore.Steam, cancellationToken)
            .ConfigureAwait(false);

        var games = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            foreach (string library in await ReadLibraryFoldersAsync(root, cancellationToken).ConfigureAwait(false))
            {
                await ReadManifestsAsync(library, games, cancellationToken).ConfigureAwait(false);
            }
        }

        return [.. games.Values];
    }

    /// <summary>Extracts the library folder paths from a <c>libraryfolders.vdf</c> document.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>The library paths, in the order the file lists them.</returns>
    /// <remarks>
    /// Two layouts exist in the wild. The older one maps an index straight to a path string; the
    /// current one maps an index to a block with a <c>path</c> key. Both are handled, because a
    /// machine that has been upgraded in place can still carry the old file.
    /// </remarks>
    public static IReadOnlyList<string> ParseLibraryFolders(KeyValueNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        KeyValueNode root = document.Child("libraryfolders") ?? document;
        var paths = new List<string>();

        foreach (KeyValuePair<string, KeyValueNode> entry in root.Children)
        {
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            string? path = entry.Value.Value ?? entry.Value.ValueAt("path");

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>Builds an installation from a parsed <c>appmanifest_*.acf</c> document.</summary>
    /// <param name="document">Parsed manifest.</param>
    /// <param name="libraryPath">Library folder the manifest belongs to.</param>
    /// <param name="fileSystem">File access, used to build the install path.</param>
    /// <returns>The installation, or <see langword="null"/> when the manifest lacks an id or name.</returns>
    public static GameInstallation? ParseAppManifest(
        KeyValueNode document,
        string libraryPath,
        IGameFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fileSystem);

        KeyValueNode? state = document.Child("AppState");
        string? appId = state?.ValueAt("appid");
        string? name = state?.ValueAt("name");

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? installDir = state?.ValueAt("installdir");
        string? installPath = string.IsNullOrWhiteSpace(installDir)
            ? null
            : fileSystem.Combine(libraryPath, "steamapps", "common", installDir);

        long? size = long.TryParse(
            state?.ValueAt("SizeOnDisk"), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

        return new GameInstallation
        {
            Id = GameIdentity.ForStoreApp(GameStore.Steam, appId),
            Name = name,
            Store = GameStore.Steam,
            StoreAppId = appId,
            InstallDirectory = installPath,

            // Not a guess: Steam's own protocol handler launches the game exactly as the user's
            // shortcut does, including whatever overlay and DRM setup the title needs.
            LaunchCommand = $"steam://rungameid/{appId}",
            SizeBytes = size,
        };
    }

    private async Task<IReadOnlyList<string>> ReadLibraryFoldersAsync(
        string root,
        CancellationToken cancellationToken)
    {
        // The root itself is always a library, whether or not the file lists it.
        var libraries = new List<string> { root };

        string manifest = _fileSystem.Combine(root, "steamapps", "libraryfolders.vdf");

        if (!_fileSystem.FileExists(manifest))
        {
            return libraries;
        }

        string? text = await _fileSystem.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false);

        foreach (string path in ParseLibraryFolders(ValveKeyValueParser.Parse(text)))
        {
            if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(path);
            }
        }

        return libraries;
    }

    private async Task ReadManifestsAsync(
        string libraryPath,
        Dictionary<string, GameInstallation> games,
        CancellationToken cancellationToken)
    {
        string steamApps = _fileSystem.Combine(libraryPath, "steamapps");

        if (!_fileSystem.DirectoryExists(steamApps))
        {
            return;
        }

        foreach (string file in _fileSystem.EnumerateFiles(steamApps, "appmanifest_*.acf"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? text = await _fileSystem.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            GameInstallation? game = ParseAppManifest(
                ValveKeyValueParser.Parse(text), libraryPath, _fileSystem);

            if (game is null)
            {
                _logger.LogDebug("Skipping Steam manifest {File}: it names no app id or title.", file);
                continue;
            }

            games[game.Id] = game;
        }
    }
}
