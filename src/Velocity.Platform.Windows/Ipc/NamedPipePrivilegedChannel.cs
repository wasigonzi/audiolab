using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Ipc;
using Velocity.Ipc.Protocol;

namespace Velocity.Platform.Windows.Ipc;

/// <summary>
/// Connects to the privileged helper over a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// The connection is established lazily and re-established after a failure, because the helper
/// service can be stopped, restarted or upgraded while the desktop application is open. A broken
/// channel degrades the product to read only rather than breaking it: tweaks that need elevation
/// report themselves as unavailable.
/// </para>
/// <para>
/// The pipe is opened with <see cref="TokenImpersonationLevel.Anonymous"/> so that the SYSTEM
/// service cannot impersonate the calling user's token. The helper does not need the caller's
/// identity to do its work, and not handing it over removes a whole class of confused deputy
/// problem.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NamedPipePrivilegedChannel : IPrivilegedChannel, IDisposable
{
    private readonly NamedPipeChannelOptions _options;
    private readonly ILogger<NamedPipePrivilegedChannel> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private IpcClient? _client;
    private bool _disposed;

    /// <summary>Creates the channel.</summary>
    /// <param name="options">Pipe settings.</param>
    /// <param name="logger">Logger.</param>
    public NamedPipePrivilegedChannel(NamedPipeChannelOptions options, ILogger<NamedPipePrivilegedChannel> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsAvailable => _pipe?.IsConnected == true || CanConnect();

    /// <inheritdoc />
    public async Task<IpcResponse> InvokeAsync(
        string operation,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IpcClient client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await client.InvokeAsync(operation, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "The privileged channel dropped; it will be re-established on the next call.");
            ResetConnection();
            throw;
        }
    }

    private async Task<IpcClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && _pipe?.IsConnected == true)
        {
            return _client;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null && _pipe?.IsConnected == true)
            {
                return _client;
            }

            ResetConnection();

            var pipe = new NamedPipeClientStream(
                ".",
                _options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);

            await pipe.ConnectAsync(_options.ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

            _pipe = pipe;
            _client = new IpcClient(pipe, ownsStream: false);
            return _client;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private bool CanConnect()
    {
        // Probing for the pipe's existence is cheaper and less disruptive than opening a
        // connection, and it is called from a property the UI binds to.
        try
        {
            return File.Exists($@"\\.\pipe\{_options.PipeName}");
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void ResetConnection()
    {
        _client?.Dispose();
        _pipe?.Dispose();
        _client = null;
        _pipe = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();
        _connectionLock.Dispose();
    }
}
