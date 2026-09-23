using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;

namespace Velocity.Core.Hardware;

/// <summary>
/// Composes the individual probes into a <see cref="SystemProfile"/> and caches the result.
/// </summary>
/// <remarks>
/// <para>
/// A probe that throws degrades the profile instead of failing the application: the exception is
/// recorded in <see cref="SystemProfile.ProbeFailures"/> and the affected subsystem reports itself
/// as unavailable. Modules that need the missing data declare themselves incompatible rather than
/// operating on a guess, which is the difference between a degraded product and a dangerous one.
/// </para>
/// <para>
/// The three probes the engine cannot work without (processor, operating system and memory) are
/// required constructor dependencies; everything else is optional so that a platform which cannot
/// implement a probe simply does not register it.
/// </para>
/// </remarks>
public sealed class SystemProfileProvider : ISystemProfileProvider
{
    private readonly ICpuTopologyProbe _cpuProbe;
    private readonly IOperatingSystemProbe _osProbe;
    private readonly IMemoryProbe _memoryProbe;
    private readonly IGpuProbe? _gpuProbe;
    private readonly IStorageProbe? _storageProbe;
    private readonly INetworkProbe? _networkProbe;
    private readonly IDisplayProbe? _displayProbe;
    private readonly IPowerProbe? _powerProbe;
    private readonly IPlatformSecurityProbe? _securityProbe;
    private readonly IMachineKindProbe? _machineKindProbe;
    private readonly ILogger<SystemProfileProvider> _logger;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    private SystemProfile? _cached;

    /// <summary>Creates the provider.</summary>
    /// <param name="cpuProbe">Processor topology probe.</param>
    /// <param name="osProbe">Operating system probe.</param>
    /// <param name="memoryProbe">Memory probe.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gpuProbe">Optional display adapter probe.</param>
    /// <param name="storageProbe">Optional storage probe.</param>
    /// <param name="networkProbe">Optional network probe.</param>
    /// <param name="displayProbe">Optional monitor probe.</param>
    /// <param name="powerProbe">Optional power probe.</param>
    /// <param name="securityProbe">Optional platform security probe.</param>
    /// <param name="machineKindProbe">Optional chassis probe.</param>
    public SystemProfileProvider(
        ICpuTopologyProbe cpuProbe,
        IOperatingSystemProbe osProbe,
        IMemoryProbe memoryProbe,
        ILogger<SystemProfileProvider> logger,
        IGpuProbe? gpuProbe = null,
        IStorageProbe? storageProbe = null,
        INetworkProbe? networkProbe = null,
        IDisplayProbe? displayProbe = null,
        IPowerProbe? powerProbe = null,
        IPlatformSecurityProbe? securityProbe = null,
        IMachineKindProbe? machineKindProbe = null)
    {
        _cpuProbe = cpuProbe ?? throw new ArgumentNullException(nameof(cpuProbe));
        _osProbe = osProbe ?? throw new ArgumentNullException(nameof(osProbe));
        _memoryProbe = memoryProbe ?? throw new ArgumentNullException(nameof(memoryProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuProbe = gpuProbe;
        _storageProbe = storageProbe;
        _networkProbe = networkProbe;
        _displayProbe = displayProbe;
        _powerProbe = powerProbe;
        _securityProbe = securityProbe;
        _machineKindProbe = machineKindProbe;
    }

    /// <inheritdoc />
    public async Task<SystemProfile> GetAsync(CancellationToken cancellationToken)
    {
        SystemProfile? cached = Volatile.Read(ref _cached);
        return cached ?? await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SystemProfile> RefreshAsync(CancellationToken cancellationToken)
    {
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SystemProfile profile = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, profile);
            return profile;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private async Task<SystemProfile> CaptureAsync(CancellationToken cancellationToken)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);

        CpuTopology cpu = await RunRequiredAsync(_cpuProbe, cancellationToken).ConfigureAwait(false);
        OperatingSystemInfo os = await RunRequiredAsync(_osProbe, cancellationToken).ConfigureAwait(false);
        MemoryInfo memory = await RunRequiredAsync(_memoryProbe, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<GpuDevice> gpus =
            await RunOptionalAsync(_gpuProbe, Array.Empty<GpuDevice>(), failures, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<StorageDevice> storage =
            await RunOptionalAsync(_storageProbe, Array.Empty<StorageDevice>(), failures, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<NetworkAdapter> network =
            await RunOptionalAsync(_networkProbe, Array.Empty<NetworkAdapter>(), failures, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<DisplayDevice> displays =
            await RunOptionalAsync(_displayProbe, Array.Empty<DisplayDevice>(), failures, cancellationToken)
                .ConfigureAwait(false);
        PowerConfiguration power =
            await RunOptionalAsync(_powerProbe, UnknownPowerConfiguration, failures, cancellationToken)
                .ConfigureAwait(false);
        PlatformSecurityInfo security =
            await RunOptionalAsync(_securityProbe, new PlatformSecurityInfo(), failures, cancellationToken)
                .ConfigureAwait(false);
        MachineKind machineKind =
            await RunOptionalAsync(_machineKindProbe, MachineKind.Unknown, failures, cancellationToken)
                .ConfigureAwait(false);

        return new SystemProfile
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            OperatingSystem = os,
            MachineKind = machineKind,
            Cpu = cpu,
            Memory = memory,
            Gpus = gpus,
            StorageDevices = storage,
            NetworkAdapters = network,
            Displays = displays,
            Power = power,
            PlatformSecurity = security,
            Fingerprint = HardwareFingerprintFactory.Create(cpu, gpus, memory, os),
            ProbeFailures = failures,
        };
    }

    private static PowerConfiguration UnknownPowerConfiguration => new()
    {
        ActiveScheme = new PowerScheme(Guid.Empty, "Unknown", IsActive: true),
    };

    private static async Task<TResult> RunRequiredAsync<TResult>(
        IHardwareProbe<TResult> probe,
        CancellationToken cancellationToken) =>
        await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

    private async Task<TResult> RunOptionalAsync<TResult>(
        IHardwareProbe<TResult>? probe,
        TResult fallback,
        Dictionary<string, string> failures,
        CancellationToken cancellationToken)
    {
        if (probe is null)
        {
            return fallback;
        }

        try
        {
            return await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A probe failure must degrade the profile, never take the application down with it.
            _logger.LogWarning(ex, "Hardware probe {Probe} failed; continuing without it.", probe.ProbeName);
            failures[probe.ProbeName] = ex.Message;
            return fallback;
        }
    }
}
