using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Games;
using Velocity.Core.Games;

namespace Velocity.TestSupport;

/// <summary>An in-memory file system holding store manifests.</summary>
/// <remarks>
/// Paths are compared case insensitively with backslash separators, as Windows does, so the same
/// manifest text exercises the same parsing on any build agent.
/// </remarks>
public sealed class FakeGameFileSystem : IGameFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a file, creating its parent directories.</summary>
    /// <param name="path">File path.</param>
    /// <param name="content">File content.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeGameFileSystem AddFile(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalized = Normalize(path);
        _files[normalized] = content;

        string current = normalized;
        while (true)
        {
            int separator = current.LastIndexOf('\\');
            if (separator <= 0)
            {
                break;
            }

            current = current[..separator];
            _directories.Add(current);
        }

        return this;
    }

    /// <summary>Adds an empty directory.</summary>
    /// <param name="path">Directory path.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeGameFileSystem AddDirectory(string path)
    {
        _directories.Add(Normalize(path));
        return this;
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern)
    {
        string directory = Normalize(path);
        string prefix = directory + "\\";

        string[] pattern = searchPattern.Split('*', StringSplitOptions.None);
        string start = pattern.Length > 0 ? pattern[0] : string.Empty;
        string end = pattern.Length > 1 ? pattern[^1] : string.Empty;

        return
        [
            .. _files.Keys
                .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                               !file[prefix.Length..].Contains('\\', StringComparison.Ordinal))
                .Where(file =>
                {
                    string name = file[prefix.Length..];
                    return name.StartsWith(start, StringComparison.OrdinalIgnoreCase) &&
                           name.EndsWith(end, StringComparison.OrdinalIgnoreCase) &&
                           name.Length >= start.Length + end.Length;
                })
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        string prefix = Normalize(path) + "\\";

        return
        [
            .. _directories
                .Where(directory => directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                    !directory[prefix.Length..].Contains('\\', StringComparison.Ordinal))
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <inheritdoc />
    public Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryGetValue(Normalize(path), out string? content) ? content : null);

    /// <inheritdoc />
    public string Combine(params string[] segments) =>
        Normalize(string.Join('\\', segments.Where(segment => !string.IsNullOrEmpty(segment))));

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').Replace("\\\\", "\\", StringComparison.Ordinal);
}

/// <summary>A locator returning fixed roots.</summary>
public sealed class FakeStoreLocator : IStoreLocator
{
    private readonly Dictionary<GameStore, IReadOnlyList<string>> _roots = [];

    /// <summary>Sets the roots for a store.</summary>
    /// <param name="store">Store.</param>
    /// <param name="roots">Roots to return.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeStoreLocator WithRoots(GameStore store, params string[] roots)
    {
        _roots[store] = roots;
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetStoreRootsAsync(
        GameStore store,
        CancellationToken cancellationToken) =>
        Task.FromResult(_roots.TryGetValue(store, out IReadOnlyList<string>? roots) ? roots : []);
}

/// <summary>A library source returning a fixed list, or throwing.</summary>
/// <param name="Store">Store this source reports.</param>
/// <param name="Games">Games to return.</param>
/// <param name="Failure">When set, the exception the source throws instead of returning.</param>
public sealed record FakeGameLibrarySource(
    GameStore Store,
    IReadOnlyList<GameInstallation> Games,
    Exception? Failure = null) : IGameLibrarySource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<GameInstallation>> ScanAsync(CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(Games) : Task.FromException<IReadOnlyList<GameInstallation>>(Failure);
}

/// <summary>A game library returning a fixed scan.</summary>
/// <param name="Scan">Scan to return.</param>
public sealed record FakeGameLibrary(GameLibraryScan Scan) : IGameLibrary
{
    /// <inheritdoc />
    public Task<GameLibraryScan> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(Scan);

    /// <inheritdoc />
    public Task<GameLibraryScan> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Scan);
}

/// <summary>A user-declared game store backed by a set.</summary>
public sealed class FakeUserDeclaredGames : IUserDeclaredGames
{
    private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the store.</summary>
    /// <param name="declared">Executables to report as declared.</param>
    public FakeUserDeclaredGames(params string[] declared) => _declared.UnionWith(declared);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> GetDeclaredExecutablesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(_declared);

    /// <inheritdoc />
    public Task DeclareAsync(string executableName, bool isGame, CancellationToken cancellationToken)
    {
        if (isGame)
        {
            _declared.Add(executableName);
        }
        else
        {
            _declared.Remove(executableName);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A launcher with a scripted result.</summary>
public sealed class FakeGameLauncher : IGameLauncher
{
    private readonly GameLaunchResult _result;

    /// <summary>Creates the launcher.</summary>
    /// <param name="result">Result to return.</param>
    public FakeGameLauncher(GameLaunchResult result) => _result = result;

    /// <summary>Requests received, so a test can assert what was launched and how.</summary>
    public List<GameLaunchRequest> Requests { get; } = [];

    /// <inheritdoc />
    public Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_result);
    }
}

/// <summary>A detector returning a fixed list.</summary>
/// <param name="Games">Games to report as running.</param>
public sealed record FakeGameDetector(IReadOnlyList<DetectedGame> Games) : IGameDetector
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DetectedGame>> DetectRunningGamesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Games);
}
