using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Services;

namespace Velocity.Platform.Windows.Services;

/// <summary>Starts and stops services through the service control manager.</summary>
/// <remarks>
/// <para>
/// The blocking waits in <see cref="ServiceController"/> are run on the thread pool so a stubborn
/// service cannot block the caller's thread, and every call is bounded by an explicit timeout.
/// A service that will not stop within the timeout is reported as not stopped rather than waited
/// on indefinitely: the session should start anyway.
/// </para>
/// <para>
/// There is deliberately no method here to change a start mode or disable a service.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController : IServiceController
{
    private readonly ILogger<WindowsServiceController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsServiceController(ILogger<WindowsServiceController> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<bool> StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken) =>
        ControlAsync(name, timeout, cancellationToken, (controller, wait) =>
        {
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return true;
            }

            if (!controller.CanStop)
            {
                return false;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, wait);
            return controller.Status == ServiceControllerStatus.Stopped;
        });

    /// <inheritdoc />
    public Task<bool> StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken) =>
        ControlAsync(name, timeout, cancellationToken, (controller, wait) =>
        {
            if (controller.Status == ServiceControllerStatus.Running)
            {
                return true;
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, wait);
            return controller.Status == ServiceControllerStatus.Running;
        });

    private Task<bool> ControlAsync(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<ServiceController, TimeSpan, bool> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Task.Run(
            () =>
            {
                try
                {
                    using var controller = new ServiceController(name);
                    return action(controller, timeout);
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    _logger.LogWarning(
                        "Service {Service} did not reach the requested state within {Timeout}.", name, timeout);
                    return false;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                               or ArgumentException)
                {
                    _logger.LogWarning(ex, "Could not control service {Service}.", name);
                    return false;
                }
            },
            cancellationToken);
    }
}
