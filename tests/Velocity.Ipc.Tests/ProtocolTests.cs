using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Ipc.Framing;
using Velocity.Ipc.Protocol;

namespace Velocity.Ipc.Tests;

/// <summary>Tests for the wire format.</summary>
public sealed class MessageFramerTests
{
    [Fact]
    public async Task Request_RoundTrips()
    {
        using var pair = new DuplexStreamPair();
        var request = new IpcRequest
        {
            RequestId = "abc",
            Sequence = 1,
            Operation = IpcOperations.RegistryRead,
            Arguments = new Dictionary<string, string> { ["hive"] = "HKLM" },
            IssuedAtUtc = DateTimeOffset.UtcNow,
        };

        await MessageFramer.WriteRequestAsync(pair.ClientSide, request, CancellationToken.None);
        IpcRequest? received = await MessageFramer.ReadRequestAsync(pair.ServerSide, CancellationToken.None);

        Assert.Equal("abc", received!.RequestId);
        Assert.Equal("HKLM", received.Arguments["hive"]);
    }

    [Fact]
    public async Task Response_RoundTrips()
    {
        using var pair = new DuplexStreamPair();
        IpcResponse response = IpcResponse.Fail("abc", IpcErrorCodes.Forbidden, "Not allowed.");

        await MessageFramer.WriteResponseAsync(pair.ServerSide, response, CancellationToken.None);
        IpcResponse? received = await MessageFramer.ReadResponseAsync(pair.ClientSide, CancellationToken.None);

        Assert.False(received!.Success);
        Assert.Equal(IpcErrorCodes.Forbidden, received.ErrorCode);
    }

    [Fact]
    public async Task CleanDisconnect_ReadsAsNull()
    {
        using var pair = new DuplexStreamPair();
        pair.CompleteClientWrites();

        Assert.Null(await MessageFramer.ReadRequestAsync(pair.ServerSide, CancellationToken.None));
    }

    [Fact]
    public async Task AnOversizedLengthPrefix_IsRejectedBeforeAllocating()
    {
        using var pair = new DuplexStreamPair();

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, int.MaxValue);
        await pair.ClientSide.WriteAsync(prefix, CancellationToken.None);
        await pair.ClientSide.FlushAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => MessageFramer.ReadRequestAsync(pair.ServerSide, CancellationToken.None));
    }

    [Fact]
    public async Task ANegativeLengthPrefix_IsRejected()
    {
        using var pair = new DuplexStreamPair();

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, -1);
        await pair.ClientSide.WriteAsync(prefix, CancellationToken.None);
        await pair.ClientSide.FlushAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => MessageFramer.ReadRequestAsync(pair.ServerSide, CancellationToken.None));
    }

    [Fact]
    public async Task ATruncatedFrame_IsRejected()
    {
        using var pair = new DuplexStreamPair();

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, 64);
        await pair.ClientSide.WriteAsync(prefix, CancellationToken.None);
        await pair.ClientSide.WriteAsync(new byte[10], CancellationToken.None);
        await pair.ClientSide.FlushAsync(CancellationToken.None);
        pair.CompleteClientWrites();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => MessageFramer.ReadRequestAsync(pair.ServerSide, CancellationToken.None));
    }
}

/// <summary>Tests for request validation and dispatch inside the helper.</summary>
public sealed class IpcServerTests
{
    [Fact]
    public async Task AKnownOperation_ReachesItsHandler()
    {
        var handler = new RecordingHandler("test.op");
        IpcResponse response = await SendAsync(handler, Request(1, "test.op"));

        Assert.True(response.Success);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AnUnknownOperation_IsRefusedWithoutReachingAnyHandler()
    {
        var handler = new RecordingHandler("test.op");
        IpcResponse response = await SendAsync(handler, Request(1, "something.else"));

        Assert.Equal(IpcErrorCodes.UnknownOperation, response.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AReplayedSequenceNumber_IsRefused()
    {
        var handler = new RecordingHandler("test.op");
        IReadOnlyList<IpcResponse> responses = await SendManyAsync(
            handler,
            Request(1, "test.op"),
            Request(1, "test.op"));

        Assert.True(responses[0].Success);
        Assert.Equal(IpcErrorCodes.RejectedSequence, responses[1].ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AnOutOfOrderSequenceNumber_IsRefused()
    {
        var handler = new RecordingHandler("test.op");
        IReadOnlyList<IpcResponse> responses = await SendManyAsync(
            handler,
            Request(5, "test.op"),
            Request(4, "test.op"));

        Assert.True(responses[0].Success);
        Assert.Equal(IpcErrorCodes.RejectedSequence, responses[1].ErrorCode);
    }

    [Fact]
    public async Task AStaleRequest_IsRefused()
    {
        var handler = new RecordingHandler("test.op");
        IpcRequest stale = Request(1, "test.op") with
        {
            IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        };

        IpcResponse response = await SendAsync(handler, stale);

        Assert.Equal(IpcErrorCodes.RejectedSequence, response.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AHandlerThatThrows_BecomesAFailureResponseRatherThanADroppedConnection()
    {
        var handler = new ThrowingHandler("test.op");
        IpcResponse response = await SendAsync(handler, Request(1, "test.op"));

        Assert.Equal(IpcErrorCodes.OperationFailed, response.ErrorCode);
        Assert.Contains("deliberate", response.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoHandlersForOneOperation_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new IpcServer(
            new IIpcRequestHandler[] { new RecordingHandler("test.op"), new RecordingHandler("test.op") },
            NullLogger<IpcServer>.Instance));
    }

    private static IpcRequest Request(long sequence, string operation) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Sequence = sequence,
        Operation = operation,
        IssuedAtUtc = DateTimeOffset.UtcNow,
    };

    private static async Task<IpcResponse> SendAsync(IIpcRequestHandler handler, IpcRequest request) =>
        (await SendManyAsync(handler, request))[0];

    private static async Task<IReadOnlyList<IpcResponse>> SendManyAsync(
        IIpcRequestHandler handler,
        params IpcRequest[] requests)
    {
        using var pair = new DuplexStreamPair();
        var server = new IpcServer(new[] { handler }, NullLogger<IpcServer>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task serving = server.ServeAsync(pair.ServerSide, cancellation.Token);

        var responses = new List<IpcResponse>();
        foreach (IpcRequest request in requests)
        {
            await MessageFramer.WriteRequestAsync(pair.ClientSide, request, cancellation.Token);
            IpcResponse? response =
                await MessageFramer.ReadResponseAsync(pair.ClientSide, cancellation.Token);
            responses.Add(response!);
        }

        pair.CompleteClientWrites();
        await serving;

        return responses;
    }

    private sealed class RecordingHandler : IIpcRequestHandler
    {
        internal RecordingHandler(string operation) => Operation = operation;

        public string Operation { get; }

        internal int CallCount { get; private set; }

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(IpcResponse.Ok(request.RequestId));
        }
    }

    private sealed class ThrowingHandler : IIpcRequestHandler
    {
        internal ThrowingHandler(string operation) => Operation = operation;

        public string Operation { get; }

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A deliberate failure.");
    }
}
