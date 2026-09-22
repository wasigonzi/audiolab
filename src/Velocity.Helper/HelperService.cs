using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velocity.Platform.Windows.Ipc;

namespace Velocity.Helper;

/// <summary>Hosts the named pipe listener for the lifetime of the service.</summary>
[SupportedOSPlatform("windows")]
public sealed class HelperService : BackgroundService
{
    private readonly NamedPipeServerHost _pipeHost;
    private readonly ILogger<HelperService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="pipeHost">Pipe listener.</param>
    /// <param name="logger">Logger.</param>
    public HelperService(NamedPipeServerHost pipeHost, ILogger<HelperService> logger)
    {
        _pipeHost = pipeHost ?? throw new ArgumentNullException(nameof(pipeHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _pipeHost.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A helper that dies silently leaves the desktop application believing elevation is
            // available, so the failure is recorded before the host tears down.
            _logger.LogCritical(ex, "The privileged helper stopped unexpectedly.");
            throw;
        }
    }
}
