using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Velocity.Core.Games;

/// <summary>Builds the stable identifiers that bind a profile to a game.</summary>
/// <remarks>
/// A profile is worthless if the game's identifier changes between scans, so an identifier is
/// derived only from things that do not move: the store and its application id, or, for a manually
/// added game, a hash of the normalised executable path. Display names are never part of an id,
/// because a title can be renamed by a patch.
/// </remarks>
public static class GameIdentity
{
    /// <summary>Builds the identifier for a store title.</summary>
    /// <param name="store">Store the title came from.</param>
    /// <param name="appId">Store specific application identifier.</param>
    /// <returns>The identifier.</returns>
    public static string ForStoreApp(Velocity.Abstractions.Games.GameStore store, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{store.ToString().ToLowerInvariant()}:{appId.Trim()}");
    }

    /// <summary>Builds the identifier for a game identified only by its executable.</summary>
    /// <param name="executablePath">Full path to the executable.</param>
    /// <returns>The identifier.</returns>
    /// <remarks>
    /// The path is hashed rather than stored, so a profile list cannot leak a user name that
    /// happens to appear in an install path.
    /// </remarks>
    public static string ForExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string normalized = executablePath.Replace('/', '\\').Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"exe:{Convert.ToHexStringLower(hash)[..16]}");
    }

    /// <summary>Extracts the file name from a Windows or POSIX path.</summary>
    /// <param name="path">Path to split.</param>
    /// <returns>The final segment, or the input when it has no separator.</returns>
    /// <remarks>
    /// <see cref="System.IO.Path.GetFileName(string)"/> does not treat a backslash as a separator
    /// on Linux, and these paths come from Windows manifests whichever machine parses them.
    /// </remarks>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string trimmed = path.Trim().TrimEnd('\\', '/');
        int separator = trimmed.LastIndexOfAny(['\\', '/']);

        return separator >= 0 && separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : trimmed;
    }

    /// <summary>Whether a path sits inside a directory, comparing case insensitively.</summary>
    /// <param name="path">Path to test.</param>
    /// <param name="directory">Candidate parent directory.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is under the directory.</returns>
    public static bool IsUnder(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
        string normalizedDirectory = directory.Replace('/', '\\').TrimEnd('\\');

        return normalizedPath.StartsWith(
            normalizedDirectory + '\\', StringComparison.OrdinalIgnoreCase);
    }
}
