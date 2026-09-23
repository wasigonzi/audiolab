using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Ipc.Tests;

/// <summary>
/// A pair of connected in-memory duplex streams, standing in for the named pipe.
/// </summary>
/// <remarks>
/// The protocol is transport independent, so the server and client code exercised here is exactly
/// the code that runs over the real pipe. Only the bytes' route between them differs.
/// </remarks>
internal sealed class DuplexStreamPair : IDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();

    internal DuplexStreamPair()
    {
        ClientSide = new CombinedStream(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());
        ServerSide = new CombinedStream(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream());
    }

    internal Stream ClientSide { get; }

    internal Stream ServerSide { get; }

    /// <summary>Closes the client's write side, which the server sees as a disconnect.</summary>
    internal void CompleteClientWrites() => _clientToServer.Writer.Complete();

    public void Dispose()
    {
        ClientSide.Dispose();
        ServerSide.Dispose();
    }

    private sealed class CombinedStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        internal CombinedStream(Stream read, Stream write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _read.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _write.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _write.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
