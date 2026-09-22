using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Services;

/// <summary>Whether a service is running.</summary>
public enum ServiceState
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Not running.</summary>
    Stopped = 1,

    /// <summary>Starting or stopping.</summary>
    Transitioning = 2,

    /// <summary>Running.</summary>
    Running = 3,

    /// <summary>Running but suspended.</summary>
    Paused = 4,
}

/// <summary>How a service starts.</summary>
public enum ServiceStartMode
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Started by the kernel at boot. Never changed by this product.</summary>
    Boot = 1,

    /// <summary>Started during system initialisation. Never changed by this product.</summary>
    System = 2,

    /// <summary>Started automatically when Windows starts.</summary>
    Automatic = 3,

    /// <summary>Started on demand.</summary>
    Manual = 4,

    /// <summary>Cannot be started.</summary>
    Disabled = 5,
}

/// <summary>What kind of thing a service is.</summary>
public enum ServiceKind
{
    /// <summary>A user mode service.</summary>
    Service = 0,

    /// <summary>A kernel driver. Never touched.</summary>
    KernelDriver = 1,

    /// <summary>A file system driver. Never touched.</summary>
    FileSystemDriver = 2,
}

/// <summary>
/// How the optimizer classifies a service.
/// </summary>
/// <remarks>
/// The classification is derived per machine, primarily from the dependency graph and the service's
/// own properties. It is not a shipped list of service names to disable, which is the thing this
/// product exists not to be.
/// </remarks>
public enum ServiceClassification
{
    /// <summary>Not classified.</summary>
    Unknown = 0,

    /// <summary>Windows does not function correctly without it. Never touched.</summary>
    Critical = 1,

    /// <summary>Something currently running depends on it. Never touched while that is true.</summary>
    Required = 2,

    /// <summary>Backs a driver or a piece of hardware. Never touched.</summary>
    HardwareDependent = 3,

    /// <summary>Part of the gaming stack: anti-cheat, launchers, overlays. Never touched.</summary>
    GamingRelated = 4,

    /// <summary>Security or privacy relevant. Never touched.</summary>
    Security = 5,

    /// <summary>
    /// Running, nothing depends on it, and it is not needed to play a game. A candidate for being
    /// stopped for the duration of a session and started again afterwards.
    /// </summary>
    Optional = 6,

    /// <summary>Already stopped, so there is nothing to gain from it.</summary>
    AlreadyStopped = 7,
}

/// <summary>One Windows service.</summary>
public sealed record ServiceSnapshot
{
    /// <summary>Service name, as the service control manager knows it.</summary>
    public required string Name { get; init; }

    /// <summary>Name shown to the user.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Current state.</summary>
    public required ServiceState State { get; init; }

    /// <summary>Configured start mode.</summary>
    public required ServiceStartMode StartMode { get; init; }

    /// <summary>What kind of thing it is.</summary>
    public ServiceKind Kind { get; init; } = ServiceKind.Service;

    /// <summary>Services this one needs.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    /// <summary>Services that need this one.</summary>
    public IReadOnlyList<string> DependentServices { get; init; } = Array.Empty<string>();

    /// <summary>Account the service runs as.</summary>
    public string? Account { get; init; }

    /// <summary>Description published by the service.</summary>
    public string? Description { get; init; }

    /// <summary>Image path of the hosting executable.</summary>
    public string? ImagePath { get; init; }

    /// <summary><see langword="true"/> when the binary is shipped by Microsoft.</summary>
    public bool IsMicrosoftService { get; init; }

    /// <summary>How the optimizer classifies it on this machine.</summary>
    public ServiceClassification Classification { get; init; } = ServiceClassification.Unknown;

    /// <summary>Why it was classified that way, in plain language.</summary>
    public string ClassificationReason { get; init; } = string.Empty;

    /// <summary><see langword="true"/> when it may be stopped for the duration of a session.</summary>
    public bool IsSessionCandidate => Classification == ServiceClassification.Optional;
}

/// <summary>Reads the service control manager.</summary>
public interface IServiceInspector
{
    /// <summary>Enumerates every service.</summary>
    /// <param name="cancellationToken">Token used to abort the enumeration.</param>
    /// <returns>The services, unclassified.</returns>
    Task<IReadOnlyList<ServiceSnapshot>> GetServicesAsync(CancellationToken cancellationToken);

    /// <summary>Reads one service.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The service, or <see langword="null"/> when it does not exist.</returns>
    Task<ServiceSnapshot?> GetServiceAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Starts and stops services.</summary>
/// <remarks>
/// Deliberately narrow: there is no method to change a start mode. This product stops an optional
/// service for the duration of a session and starts it again afterwards; it does not disable
/// services permanently, because a disabled service is a support problem that outlives the product.
/// </remarks>
public interface IServiceController
{
    /// <summary>Stops a running service.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="timeout">How long to wait for it to stop.</param>
    /// <param name="cancellationToken">Token used to abort the wait.</param>
    /// <returns><see langword="true"/> when the service reached the stopped state.</returns>
    Task<bool> StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts a stopped service.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="timeout">How long to wait for it to start.</param>
    /// <param name="cancellationToken">Token used to abort the wait.</param>
    /// <returns><see langword="true"/> when the service reached the running state.</returns>
    Task<bool> StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);
}
