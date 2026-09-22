using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Services;
using Velocity.Abstractions.State;

namespace Velocity.Core.Services;

/// <summary>
/// Exposes service running state to the snapshot and rollback engines.
/// </summary>
/// <remarks>
/// <para>
/// Keys are <c>service://&lt;name&gt;#state</c> with a value of <c>Running</c> or <c>Stopped</c>.
/// Start mode is deliberately <em>not</em> exposed: this product stops an optional service for the
/// duration of a session and starts it again afterwards, and never reconfigures one permanently.
/// A disabled service is a support problem that outlives the product.
/// </para>
/// <para>
/// The provider re-classifies the whole service set on every write and refuses anything that is not
/// currently Optional. That check lives here rather than in a module so that a future module cannot
/// stop a critical or security service by forgetting to ask.
/// </para>
/// </remarks>
public sealed class ServiceStateProvider : IStateProvider
{
    /// <summary>Scheme this provider answers for.</summary>
    public const string Scheme = "service";

    /// <summary>Item name for a service's running state.</summary>
    public const string StateItem = "state";

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceInspector _inspector;
    private readonly IServiceController _controller;
    private readonly ILogger<ServiceStateProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="inspector">Reads the service control manager.</param>
    /// <param name="controller">Starts and stops services.</param>
    /// <param name="logger">Logger.</param>
    public ServiceStateProvider(
        IServiceInspector inspector,
        IServiceController controller,
        ILogger<ServiceStateProvider> logger)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => Scheme;

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public bool RequiresElevation(StateKey key) => true;

    /// <inheritdoc />
    public async Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken)
    {
        ServiceSnapshot? service =
            await _inspector.GetServiceAsync(key.Path, cancellationToken).ConfigureAwait(false);

        if (service is null)
        {
            return StateValue.Absent;
        }

        return key.Item == StateItem
            ? StateValue.FromString(service.State == ServiceState.Running ? "Running" : "Stopped")
            : throw new ArgumentException($"'{key.Item}' is not a service state item.", nameof(key));
    }

    /// <inheritdoc />
    public async Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        if (value.IsAbsent)
        {
            // The service did not exist when the snapshot was taken; there is nothing to restore.
            return;
        }

        bool shouldRun = string.Equals(value.Data, "Running", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<ServiceSnapshot> all =
            await _inspector.GetServicesAsync(cancellationToken).ConfigureAwait(false);

        ServiceSnapshot? service = all.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, key.Path, StringComparison.OrdinalIgnoreCase));

        if (service is null)
        {
            _logger.LogDebug("Service {Service} no longer exists; skipping.", key.Path);
            return;
        }

        if (shouldRun)
        {
            // Starting a service back up is always permitted: it returns the machine to how it was.
            if (service.State != ServiceState.Running)
            {
                await _controller.StartAsync(service.Name, ControlTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        ServiceSnapshot classified = ServiceClassifier.Classify(service, all);

        if (classified.Classification != ServiceClassification.Optional)
        {
            _logger.LogWarning(
                "Refusing to stop {Service}: {Reason}", service.Name, classified.ClassificationReason);
            return;
        }

        if (service.State == ServiceState.Running)
        {
            await _controller.StopAsync(service.Name, ControlTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
