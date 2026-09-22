using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Services;

namespace Velocity.TestSupport;

/// <summary>Builds service snapshots for tests.</summary>
public static class ServiceFixtures
{
    /// <summary>Creates a service snapshot.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="state">Running state.</param>
    /// <param name="startMode">Start mode.</param>
    /// <param name="kind">Service or driver.</param>
    /// <param name="dependsOn">Services this one needs.</param>
    /// <param name="dependents">Services that need this one.</param>
    /// <param name="displayName">Display name, defaulting to the service name.</param>
    /// <returns>The snapshot.</returns>
    public static ServiceSnapshot Service(
        string name,
        ServiceState state = ServiceState.Running,
        ServiceStartMode startMode = ServiceStartMode.Automatic,
        ServiceKind kind = ServiceKind.Service,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? dependents = null,
        string? displayName = null) => new()
        {
            Name = name,
            DisplayName = displayName ?? name,
            State = state,
            StartMode = startMode,
            Kind = kind,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            DependentServices = dependents ?? Array.Empty<string>(),
        };
}

/// <summary>A service inspector over a fixed set.</summary>
public sealed class FakeServiceInspector : IServiceInspector
{
    private readonly List<ServiceSnapshot> _services;

    /// <summary>Creates the inspector.</summary>
    /// <param name="services">Services to report.</param>
    public FakeServiceInspector(params ServiceSnapshot[] services) => _services = services.ToList();

    /// <summary>The services currently reported.</summary>
    public IList<ServiceSnapshot> Services => _services;

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceSnapshot>> GetServicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceSnapshot>>(_services.ToList());

    /// <inheritdoc />
    public Task<ServiceSnapshot?> GetServiceAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_services.FirstOrDefault(service =>
            string.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Changes a service's reported state, as the controller's effect would.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="state">New state.</param>
    public void SetState(string name, ServiceState state)
    {
        for (int i = 0; i < _services.Count; i++)
        {
            if (string.Equals(_services[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _services[i] = _services[i] with { State = state };
            }
        }
    }
}

/// <summary>A service controller that records calls and reflects them into the inspector.</summary>
public sealed class FakeServiceController : Velocity.Abstractions.Services.IServiceController
{
    private readonly FakeServiceInspector _inspector;

    /// <summary>Creates the controller.</summary>
    /// <param name="inspector">Inspector whose state the controller mutates.</param>
    public FakeServiceController(FakeServiceInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <summary>Services that were stopped, in order.</summary>
    public List<string> Stopped { get; } = new();

    /// <summary>Services that were started, in order.</summary>
    public List<string> Started { get; } = new();

    /// <summary>Services the controller should report failure for.</summary>
    public HashSet<string> Stubborn { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<bool> StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Stubborn.Contains(name))
        {
            return Task.FromResult(false);
        }

        Stopped.Add(name);
        _inspector.SetState(name, ServiceState.Stopped);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Started.Add(name);
        _inspector.SetState(name, ServiceState.Running);
        return Task.FromResult(true);
    }
}
