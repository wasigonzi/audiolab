using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Games;

/// <summary>
/// The narrow slice of the file system the library scanners need.
/// </summary>
/// <remarks>
/// Scanners read store manifests, which is ordinary parsing work with no Windows API in it. Putting
/// the file access behind this interface keeps the parsers in the portable assembly, where they are
/// tested against real manifest text on any machine, instead of being untestable code that only
/// runs on a developer's gaming PC.
/// </remarks>
public interface IGameFileSystem
{
    /// <summary>Whether a directory exists.</summary>
    /// <param name="path">Directory path.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Whether a file exists.</summary>
    /// <param name="path">File path.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    bool FileExists(string path);

    /// <summary>Lists the files in a directory matching a pattern, without recursing.</summary>
    /// <param name="path">Directory to list.</param>
    /// <param name="searchPattern">File name pattern, for example <c>*.acf</c>.</param>
    /// <returns>Full paths, or an empty list when the directory is unreadable.</returns>
    IReadOnlyList<string> EnumerateFiles(string path, string searchPattern);

    /// <summary>Lists the immediate subdirectories of a directory.</summary>
    /// <param name="path">Directory to list.</param>
    /// <returns>Full paths, or an empty list when the directory is unreadable.</returns>
    IReadOnlyList<string> EnumerateDirectories(string path);

    /// <summary>Reads a text file.</summary>
    /// <param name="path">File path.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The contents, or <see langword="null"/> when the file cannot be read.</returns>
    Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Joins path segments using the platform separator.</summary>
    /// <param name="segments">Segments to join.</param>
    /// <returns>The combined path.</returns>
    string Combine(params string[] segments);
}

/// <summary>Tells the scanners where a store keeps its data on this machine.</summary>
/// <remarks>
/// Locating Steam or Epic means reading the registry or a well known program data folder, which is
/// platform specific; parsing what is found there is not. The split follows that line.
/// </remarks>
public interface IStoreLocator
{
    /// <summary>
    /// Returns the root directories for a store, most authoritative first.
    /// </summary>
    /// <param name="store">Store to locate.</param>
    /// <param name="cancellationToken">Token used to abort the lookup.</param>
    /// <returns>
    /// The roots found. An empty list means the store is not installed, which is not an error.
    /// </returns>
    Task<IReadOnlyList<string>> GetStoreRootsAsync(GameStore store, CancellationToken cancellationToken);
}
