using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Games;

namespace Velocity.Platform.Windows.Games;

/// <summary>File access for the library scanners, backed by the real file system.</summary>
/// <remarks>
/// Every method swallows the access failures a library scan routinely hits: a library folder on a
/// disconnected external drive, a manifest folder the current user cannot read, a path left behind
/// by an uninstalled store. None of those is an error worth failing a scan over, so they produce an
/// empty result and a debug log line.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameFileSystem : IGameFileSystem
{
    private readonly ILogger<WindowsGameFileSystem> _logger;

    /// <summary>Creates the file system.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsGameFileSystem(ILogger<WindowsGameFileSystem> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            _logger.LogDebug("Could not list '{Path}': {Reason}", path, ex.Message);
            return [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            _logger.LogDebug("Could not list '{Path}': {Reason}", path, ex.Message);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            _logger.LogDebug("Could not read '{Path}': {Reason}", path, ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public string Combine(params string[] segments) => Path.Combine(segments);
}
