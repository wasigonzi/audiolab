using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Ipc.Framing;
using Velocity.Ipc.Protocol;

namespace Velocity.Ipc;

/// <summary>Tuning for the request dispatcher.</summary>
public sealed class IpcServerOptions
{
    /// <summary>
    /// How far a request timestamp may deviate from the helper's clock before it is refused.
    /// </summary>
    /// <remarks>
    /// On a local named pipe protected by an ACL this is defence in depth rather than the primary
    /// control: it bounds the window in which a captured frame is useful, on top of the per
    /// connection sequence check that already rejects an exact replay.
    /// </remarks>
    public TimeSpan MaximumRequestAge { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a single handler may run before the dispatcher abandons it.</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads requests from one connection, validates them and dispatches them to handlers.
/// </summary>
/// <remarks>
/// <para>
/// The server is transport agnostic; the helper hands it an accepted named pipe stream and the
/// tests hand it an in-memory stream pair, so the validation logic under test is the same code
/// that runs in production.
/// </para>
/// <para>
/// Validation happens in a fixed order: the operation must be registered, the sequence number must
/// be new and increasing, the timestamp must be current. Only then does a handler see the request.
/// </para>
/// </remarks>
public sealed class IpcServer
{
    private readonly Dictionary<string, IIpcRequestHandler> _handlers;
    private readonly ILogger<IpcServer> _logger;
    private readonly IpcServerOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the server.</summary>
    /// <param name="handlers">Handlers to expose.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Dispatcher options.</param>
    /// <param name="timeProvider">Clock, injected so tests can exercise the staleness check.</param>
    /// <exception cref="ArgumentException">Two handlers claim the same operation.</exception>
    public IpcServer(
        IEnumerable<IIpcRequestHandler> handlers,
        ILogger<IpcServer> logger,
        IpcServerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new IpcServerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _handlers = new Dictionary<string, IIpcRequestHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (IIpcRequestHandler handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Operation, handler))
            {
                throw new ArgumentException(
                    $"More than one handler claims operation '{handler.Operation}'.", nameof(handlers));
            }
        }
    }

    /// <summary>Serves requests on one connection until the peer disconnects.</summary>
    /// <param name="stream">The accepted connection.</param>
    /// <param name="cancellationToken">Token used to stop serving.</param>
    /// <returns>A task that completes when the connection closes.</returns>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long lastSequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            IpcRequest? request;

            try
            {
                request = await MessageFramer.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                // A malformed frame means the connection is no longer trustworthy: drop it rather
                // than trying to resynchronise on a stream an attacker may be shaping.
                _logger.LogWarning(ex, "Dropping IPC connection after a malformed frame.");
                return;
            }

            if (request is null)
            {
                return;
            }

            IpcResponse response = await ProcessAsync(request, lastSequence, cancellationToken)
                .ConfigureAwait(false);

            if (request.Sequence > lastSequence)
            {
                lastSequence = request.Sequence;
            }

            await MessageFramer.WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IpcResponse> ProcessAsync(
        IpcRequest request,
        long lastSequence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return IpcResponse.Fail("unknown", IpcErrorCodes.InvalidArguments, "Request id is required.");
        }

        if (request.Sequence <= lastSequence)
        {
            _logger.LogWarning(
                "Rejected IPC request {RequestId}: sequence {Sequence} is not greater than {LastSequence}.",
                request.RequestId,
                request.Sequence,
                lastSequence);

            return IpcResponse.Fail(
                request.RequestId,
                IpcErrorCodes.RejectedSequence,
                "Sequence numbers must increase; this request looks like a replay.");
        }

        TimeSpan age = _timeProvider.GetUtcNow() - request.IssuedAtUtc;
        if (age.Duration() > _options.MaximumRequestAge)
        {
            return IpcResponse.Fail(
                request.RequestId,
                IpcErrorCodes.RejectedSequence,
                "Request timestamp is outside the accepted window.");
        }

        if (!_handlers.TryGetValue(request.Operation, out IIpcRequestHandler? handler))
        {
            _logger.LogWarning(
                "Rejected unknown IPC operation '{Operation}' from request {RequestId}.",
                request.Operation,
                request.RequestId);

            return IpcResponse.Fail(
                request.RequestId,
                IpcErrorCodes.UnknownOperation,
                $"Operation '{request.Operation}' is not supported by this helper.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HandlerTimeout);

        try
        {
            return await handler.HandleAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("IPC operation {Operation} timed out.", request.Operation);
            return IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.OperationFailed, "The operation timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "IPC operation {Operation} threw.", request.Operation);
            return IpcResponse.Fail(request.RequestId, IpcErrorCodes.OperationFailed, ex.Message);
        }
    }
}
