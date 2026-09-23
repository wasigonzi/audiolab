using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Velocity.Abstractions.Hardware;

namespace Velocity.Core.Hardware;

/// <summary>
/// Builds the stable, non identifying key that scopes every stored measurement.
/// </summary>
/// <remarks>
/// <para>
/// The inputs are chosen to answer one question: "would a benchmark taken earlier still be
/// evidence about this machine today?" A driver update changes the answer, so the driver version
/// is part of the GPU hash. A different user name does not, so no user identity is included.
/// </para>
/// <para>
/// Nothing hashed here is a serial number, MAC address, machine name or user name, which is what
/// makes it safe to use the same key in a future shared hardware database.
/// </para>
/// </remarks>
public static class HardwareFingerprintFactory
{
    /// <summary>Computes the fingerprint for a machine.</summary>
    /// <param name="cpu">Processor topology.</param>
    /// <param name="gpus">Installed display adapters.</param>
    /// <param name="memory">Memory state.</param>
    /// <param name="operatingSystem">Operating system identity.</param>
    /// <returns>The fingerprint.</returns>
    public static HardwareFingerprint Create(
        CpuTopology cpu,
        IReadOnlyList<GpuDevice> gpus,
        MemoryInfo memory,
        OperatingSystemInfo operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(gpus);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(operatingSystem);

        string cpuHash = Hash(string.Create(
            CultureInfo.InvariantCulture,
            $"cpu|{cpu.Vendor}|{cpu.BrandString}|cores={cpu.PhysicalCoreCount}|threads={cpu.LogicalProcessorCount}|hybrid={cpu.IsHybrid}|groups={cpu.Groups.Count}|numa={cpu.NumaNodes.Count}"));

        // Adapters are ordered by device instance id so that PCIe enumeration order does not
        // change the fingerprint between boots.
        string gpuDescription = string.Join(
            ';',
            gpus.OrderBy(gpu => gpu.DeviceInstanceId, StringComparer.Ordinal)
                .Select(gpu => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{gpu.Vendor}:{gpu.Description}:{gpu.DriverVersion ?? "unknown"}")));
        string gpuHash = Hash($"gpu|{gpuDescription}");

        string moduleDescription = string.Join(
            ';',
            memory.Modules
                .OrderBy(module => module.Slot, StringComparer.Ordinal)
                .Select(module => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{module.CapacityBytes}@{module.ConfiguredSpeedMtps}")));
        string memoryHash = Hash(string.Create(
            CultureInfo.InvariantCulture,
            $"memory|total={memory.TotalPhysicalBytes}|modules={moduleDescription}"));

        string osHash = Hash(string.Create(
            CultureInfo.InvariantCulture,
            $"os|{operatingSystem.MajorVersion}.{operatingSystem.MinorVersion}.{operatingSystem.BuildNumber}.{operatingSystem.UpdateBuildRevision}|{operatingSystem.Architecture}"));

        return new HardwareFingerprint
        {
            CpuHash = cpuHash,
            GpuHash = gpuHash,
            MemoryHash = memoryHash,
            OsHash = osHash,
            CompositeHash = Hash($"{cpuHash}|{gpuHash}|{memoryHash}|{osHash}"),
        };
    }

    private static string Hash(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(digest)[..32];
    }
}
