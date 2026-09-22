using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Hardware;
using Velocity.Core.Hardware;

namespace Velocity.TestSupport;

/// <summary>
/// Reference machines the engine must behave correctly on.
/// </summary>
/// <remarks>
/// Each fixture reproduces the topology Windows reports for a real, widely owned configuration.
/// They are the contract the scheduling logic is tested against: an Intel desktop, an Intel hybrid
/// part, a single chiplet Ryzen, a dual chiplet Ryzen with asymmetric cache, a small laptop, and a
/// workstation large enough to span two processor groups.
/// </remarks>
public static class MachineFixtures
{
    /// <summary>Homogeneous 8 core, 16 thread Intel desktop with one 32 MB last level cache.</summary>
    /// <returns>The topology.</returns>
    public static CpuTopology IntelDesktopEightCore()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Intel, "Intel(R) Core(TM) i7-11700K")
            .AddCores(coreCount: 8, threadsPerCore: 2);

        return builder
            .AddSharedCache(3, 16 * 1024 * 1024, Enumerable.Range(0, 8))
            .Build();
    }

    /// <summary>
    /// Intel hybrid part with 8 performance cores (2 threads each) and 16 efficiency cores in
    /// clusters of four sharing an L2, all under one L3.
    /// </summary>
    /// <returns>The topology.</returns>
    public static CpuTopology IntelHybridPerformanceAndEfficiency()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Intel, "Intel(R) Core(TM) i9-13900K");

        builder.AddCores(coreCount: 8, threadsPerCore: 2, efficiencyClass: 1);
        builder.AddCores(coreCount: 16, threadsPerCore: 1, efficiencyClass: 0);

        // Efficiency cores share an L2 per cluster of four; every core shares the single L3.
        for (int cluster = 0; cluster < 4; cluster++)
        {
            int firstCore = 8 + (cluster * 4);
            builder.AddSharedCache(2, 4 * 1024 * 1024, Enumerable.Range(firstCore, 4));
        }

        return builder
            .AddSharedCache(3, 36 * 1024 * 1024, Enumerable.Range(0, 24))
            .Build();
    }

    /// <summary>Single chiplet Ryzen: 8 cores, 16 threads, one 32 MB L3.</summary>
    /// <returns>The topology.</returns>
    public static CpuTopology AmdSingleChiplet()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Amd, "AMD Ryzen 7 7800X3D 8-Core Processor")
            .AddCores(coreCount: 8, threadsPerCore: 2);

        return builder
            .AddSharedCache(3, 96 * 1024 * 1024, Enumerable.Range(0, 8))
            .Build();
    }

    /// <summary>
    /// Dual chiplet Ryzen with asymmetric cache: one chiplet carries 96 MB of stacked cache, the
    /// other 32 MB. This is the configuration where confining a game to one chiplet matters most.
    /// </summary>
    /// <returns>The topology.</returns>
    public static CpuTopology AmdDualChipletAsymmetricCache()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Amd, "AMD Ryzen 9 7950X3D 16-Core Processor")
            .AddCores(coreCount: 16, threadsPerCore: 2);

        return builder
            .AddSharedCache(3, 96 * 1024 * 1024, Enumerable.Range(0, 8))
            .AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(8, 8))
            .AddDie(0, Enumerable.Range(0, 8))
            .AddDie(1, Enumerable.Range(8, 8))
            .Build();
    }

    /// <summary>Dual chiplet Ryzen where both chiplets carry the same cache.</summary>
    /// <returns>The topology.</returns>
    public static CpuTopology AmdDualChipletSymmetricCache()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Amd, "AMD Ryzen 9 5950X 16-Core Processor")
            .AddCores(coreCount: 16, threadsPerCore: 2);

        return builder
            .AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(0, 8))
            .AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(8, 8))
            .Build();
    }

    /// <summary>Small laptop: 4 cores, 8 threads, one L3.</summary>
    /// <returns>The topology.</returns>
    public static CpuTopology QuadCoreLaptop()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Intel, "Intel(R) Core(TM) i5-8250U")
            .AddCores(coreCount: 4, threadsPerCore: 2);

        return builder
            .AddSharedCache(3, 6 * 1024 * 1024, Enumerable.Range(0, 4))
            .Build();
    }

    /// <summary>
    /// A workstation with 96 cores spread over two processor groups and two NUMA nodes, which is
    /// where group relative affinity stops being a detail.
    /// </summary>
    /// <returns>The topology.</returns>
    public static CpuTopology DualProcessorGroupWorkstation()
    {
        var builder = new CpuTopologyBuilder()
            .WithVendor(CpuVendor.Amd, "AMD EPYC 7V13 64-Core Processor");

        builder.AddCores(coreCount: 32, threadsPerCore: 1, efficiencyClass: 0, groupId: 0);
        builder.AddCores(coreCount: 32, threadsPerCore: 1, efficiencyClass: 0, groupId: 1);

        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(0, 8));
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(8, 8));
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(16, 8));
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(24, 8));
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(32, 8), groupId: 1);
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(40, 8), groupId: 1);
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(48, 8), groupId: 1);
        builder.AddSharedCache(3, 32 * 1024 * 1024, Enumerable.Range(56, 8), groupId: 1);

        builder.AddNumaNode(0, Enumerable.Range(0, 32));
        builder.AddNumaNode(1, Enumerable.Range(32, 32), groupId: 1);

        return builder.Build();
    }

    /// <summary>Wraps a topology in a complete system profile.</summary>
    /// <param name="cpu">Processor topology.</param>
    /// <param name="machineKind">Chassis class.</param>
    /// <param name="buildNumber">Windows build number.</param>
    /// <param name="gpus">Display adapters, or null for a single NVIDIA adapter.</param>
    /// <returns>The profile.</returns>
    public static SystemProfile ProfileFor(
        CpuTopology cpu,
        MachineKind machineKind = MachineKind.Desktop,
        int buildNumber = 26100,
        IReadOnlyList<GpuDevice>? gpus = null)
    {
        ArgumentNullException.ThrowIfNull(cpu);

        var operatingSystem = new OperatingSystemInfo
        {
            ProductName = "Windows 11 Pro",
            DisplayVersion = "24H2",
            MajorVersion = 10,
            MinorVersion = 0,
            BuildNumber = buildNumber,
            UpdateBuildRevision = 2314,
            Architecture = "X64",
            Uptime = TimeSpan.FromHours(6),
        };

        var memory = new MemoryInfo
        {
            TotalPhysicalBytes = 32L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 18L * 1024 * 1024 * 1024,
            CommitTotalBytes = 20L * 1024 * 1024 * 1024,
            CommitLimitBytes = 48L * 1024 * 1024 * 1024,
            PageSizeBytes = 4096,
        };

        IReadOnlyList<GpuDevice> adapters = gpus ?? new[]
        {
            new GpuDevice
            {
                DeviceInstanceId = @"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000&REV_A1\4&1234&0&0008",
                Description = "NVIDIA GeForce RTX 4090",
                Vendor = GpuVendor.Nvidia,
                DriverVersion = "32.0.15.6094",
                DedicatedVideoMemoryBytes = 24L * 1024 * 1024 * 1024,
                HardwareScheduling = HardwareSchedulingState.Enabled,
            },
        };

        var power = new PowerConfiguration
        {
            ActiveScheme = new PowerScheme(
                new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "Balanced", IsActive: true),
            ProcessorPolicy = new ProcessorPowerPolicy
            {
                MinimumProcessorStatePercent = 5,
                MaximumProcessorStatePercent = 100,
            },
            HasBattery = machineKind == MachineKind.Laptop,
        };

        return new SystemProfile
        {
            CapturedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            OperatingSystem = operatingSystem,
            MachineKind = machineKind,
            Cpu = cpu,
            Memory = memory,
            Gpus = adapters,
            Power = power,
            Fingerprint = HardwareFingerprintFactory.Create(cpu, adapters, memory, operatingSystem),
        };
    }
}
