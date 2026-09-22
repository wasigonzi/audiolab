using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// Base contract for a component that reads one aspect of the machine.
/// </summary>
/// <typeparam name="TResult">Shape of the data the probe produces.</typeparam>
/// <remarks>
/// Probes are read only by contract. Anything that changes system state is a tweak, never a probe,
/// so that "look at my machine" can be offered without elevation and without risk.
/// </remarks>
public interface IHardwareProbe<TResult>
{
    /// <summary>Stable name of the probe, used in failure reporting and logs.</summary>
    string ProbeName { get; }

    /// <summary>Reads the current state of the subsystem.</summary>
    /// <param name="cancellationToken">Token used to abort a slow probe.</param>
    /// <returns>The probe result.</returns>
    Task<TResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Reads processor topology.</summary>
public interface ICpuTopologyProbe : IHardwareProbe<CpuTopology>;

/// <summary>Reads operating system identity.</summary>
public interface IOperatingSystemProbe : IHardwareProbe<OperatingSystemInfo>;

/// <summary>Reads memory state.</summary>
public interface IMemoryProbe : IHardwareProbe<MemoryInfo>;

/// <summary>Reads installed display adapters.</summary>
public interface IGpuProbe : IHardwareProbe<System.Collections.Generic.IReadOnlyList<GpuDevice>>;

/// <summary>Reads physical storage devices.</summary>
public interface IStorageProbe : IHardwareProbe<System.Collections.Generic.IReadOnlyList<StorageDevice>>;

/// <summary>Reads network adapters and their driver exposed capabilities.</summary>
public interface INetworkProbe : IHardwareProbe<System.Collections.Generic.IReadOnlyList<NetworkAdapter>>;

/// <summary>Reads attached monitors and their supported modes.</summary>
public interface IDisplayProbe : IHardwareProbe<System.Collections.Generic.IReadOnlyList<DisplayDevice>>;

/// <summary>Reads power scheme configuration.</summary>
public interface IPowerProbe : IHardwareProbe<PowerConfiguration>;

/// <summary>Reads security and virtualization feature state.</summary>
public interface IPlatformSecurityProbe : IHardwareProbe<PlatformSecurityInfo>;

/// <summary>Reads the chassis class of the machine.</summary>
public interface IMachineKindProbe : IHardwareProbe<MachineKind>;

/// <summary>Composes the individual probes into a <see cref="SystemProfile"/>.</summary>
public interface ISystemProfileProvider
{
    /// <summary>
    /// Returns the cached profile, capturing one first if none exists.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the capture.</param>
    /// <returns>The current system profile.</returns>
    Task<SystemProfile> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Captures a fresh profile and replaces the cached one.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the capture.</param>
    /// <returns>The newly captured profile.</returns>
    Task<SystemProfile> RefreshAsync(CancellationToken cancellationToken);
}
