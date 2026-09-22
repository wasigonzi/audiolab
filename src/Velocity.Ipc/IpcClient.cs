using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Ipc.Framing;
using Velocity.Ipc.Protocol;

namespace Velocity.Ipc;

/// <summary>
/// Sends requests to the privileged helper over an established connection.
/// </summary>
/// <remarks>
/// The protocol is strict request/response with no pipelining, so calls are serialised with a
/// semaphore. That keeps the sequence numbering monotonic without needing a correlation table, and
/// privileged operations are rare enough that the lost concurrency costs nothing.
/// </remarks>
public sealed class IpcClient : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly bool _ownsStream;
    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a client over an open connection.</summary>
    /// <param name="stream">Connected duplex stream.</param>
    /// <param name="ownsStream">Whether disposing the client should dispose the stream.</param>
    public IpcClient(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>Invokes an operation and waits for its response.</summary>
    /// <param name="operation">Operation name.</param>
    /// <param name="arguments">Operation arguments.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The helper's response.</returns>
    /// <exception cref="IOException">The helper closed the connection without responding.</exception>
    public async Task<IpcResponse> InvokeAsync(
        string operation,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _callLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new IpcRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Sequence = Interlocked.Increment(ref _sequence),
                Operation = operation,
                Arguments = arguments is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase),
                IssuedAtUtc = DateTimeOffset.UtcNow,
            };

            await MessageFramer.WriteRequestAsync(_stream, request, cancellationToken).ConfigureAwait(false);

            IpcResponse? response =
                await MessageFramer.ReadResponseAsync(_stream, cancellationToken).ConfigureAwait(false);

            return response ?? throw new IOException(
                "The privileged helper closed the connection without responding.");
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _callLock.Dispose();

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
