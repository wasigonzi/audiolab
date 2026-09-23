using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Ipc.Protocol;

namespace Velocity.Ipc.Framing;

/// <summary>
/// Reads and writes length prefixed JSON messages on a duplex stream.
/// </summary>
/// <remarks>
/// <para>
/// A four byte big-endian length prefix precedes each UTF-8 JSON payload. The length is validated
/// against <see cref="MaxFrameBytes"/> before a single byte of payload is allocated, so a
/// malformed or hostile prefix cannot be used to make the helper allocate gigabytes.
/// </para>
/// <para>
/// The framer is transport independent on purpose: the production transport is an ACL protected
/// named pipe, and the tests drive exactly the same code over an in-memory stream pair.
/// </para>
/// </remarks>
public static class MessageFramer
{
    /// <summary>Largest message the framer will read or write, in bytes.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private const int PrefixBytes = 4;

    /// <summary>Writes a request.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="request">Request to write.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the frame has been flushed.</returns>
    public static Task WriteRequestAsync(Stream stream, IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, IpcJsonContext.Default.IpcRequest);
        return WriteFrameAsync(stream, payload, cancellationToken);
    }

    /// <summary>Writes a response.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="response">Response to write.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the frame has been flushed.</returns>
    public static Task WriteResponseAsync(Stream stream, IpcResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, IpcJsonContext.Default.IpcResponse);
        return WriteFrameAsync(stream, payload, cancellationToken);
    }

    /// <summary>Reads a request.</summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The request, or <see langword="null"/> when the peer closed the connection.</returns>
    /// <exception cref="InvalidDataException">The frame was malformed.</exception>
    public static async Task<IpcRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[]? payload = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcRequest)
            ?? throw new InvalidDataException("Frame did not contain a request.");
    }

    /// <summary>Reads a response.</summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The response, or <see langword="null"/> when the peer closed the connection.</returns>
    /// <exception cref="InvalidDataException">The frame was malformed.</exception>
    public static async Task<IpcResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[]? payload = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcResponse)
            ?? throw new InvalidDataException("Frame did not contain a response.");
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Message of {payload.Length} bytes exceeds the {MaxFrameBytes} byte frame limit.");
        }

        byte[] prefix = new byte[PrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[PrefixBytes];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);

        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame length {length} is outside the permitted range of 1 to {MaxFrameBytes} bytes.");
        }

        byte[] payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Stream ended in the middle of a frame.");
        }

        return payload;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                // A clean close before any byte of the frame is normal; mid-frame is not, and the
                // caller turns that into InvalidDataException.
                return read != 0
                    ? throw new InvalidDataException("Stream ended in the middle of a frame.")
                    : false;
            }

            read += chunk;
        }

        return true;
    }
}
