using System;
using System.Collections.Generic;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.Tweaks;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// Compatibility gating decides what the user is even offered. These tests cover the cases where
/// getting it wrong would mean writing a setting the machine ignores, or hiding one it supports.
/// </summary>
public sealed class CompatibilityEvaluatorTests
{
    [Fact]
    public void Supported_WhenNothingIsRequired()
    {
        CompatibilityResult result = Evaluate(Descriptor());

        Assert.True(result.IsSupported);
    }

    [Fact]
    public void Unsupported_WhenTheWindowsBuildIsTooOld()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with { MinimumWindowsBuild = 30000 });

        Assert.Equal(CompatibilityStatus.UnsupportedOperatingSystem, result.Status);
    }

    [Fact]
    public void Unsupported_WhenWindowsNoLongerHonoursTheSetting()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with { MaximumWindowsBuild = 22000 });

        Assert.Equal(CompatibilityStatus.UnsupportedOperatingSystem, result.Status);
    }

    [Fact]
    public void Unsupported_WhenElevationIsNeededAndUnavailable()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with { RequiresElevation = true },
            privileges: new FakePrivilegeContext(PrivilegeChannel.None));

        Assert.Equal(CompatibilityStatus.ElevationUnavailable, result.Status);
    }

    [Fact]
    public void Supported_WhenElevationIsNeededAndTheHelperIsRunning()
    {
        CompatibilityResult result = Evaluate(Descriptor() with { RequiresElevation = true });

        Assert.True(result.IsSupported);
    }

    [Fact]
    public void Unsupported_OnTheWrongCpuVendor()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { CpuVendors = new[] { CpuVendor.Amd } },
            });

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public void Unsupported_WhenTheProcessorIsNotHybrid()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { RequiresHybridCpu = true },
            });

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public void Supported_OnAHybridProcessorWhenOneIsRequired()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { RequiresHybridCpu = true },
            },
            topology: MachineFixtures.IntelHybridPerformanceAndEfficiency());

        Assert.True(result.IsSupported);
    }

    [Fact]
    public void Unsupported_WhenMultipleCoreComplexesAreRequiredButAbsent()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { RequiresMultipleCoreComplexes = true },
            },
            topology: MachineFixtures.AmdSingleChiplet());

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public void Supported_OnADualChipletPartWhenMultipleComplexesAreRequired()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { RequiresMultipleCoreComplexes = true },
            },
            topology: MachineFixtures.AmdDualChipletAsymmetricCache());

        Assert.True(result.IsSupported);
    }

    [Fact]
    public void Unsupported_WhenTooFewCores()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements { MinimumPhysicalCores = 12 },
            },
            topology: MachineFixtures.QuadCoreLaptop());

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public void Unsupported_OnAnExcludedMachineKind()
    {
        CompatibilityResult result = Evaluate(
            Descriptor() with
            {
                Hardware = new HardwareRequirements
                {
                    ExcludedMachineKinds = new[] { MachineKind.Laptop },
                },
            },
            machineKind: MachineKind.Laptop);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    private static CompatibilityResult Evaluate(
        TweakDescriptor descriptor,
        CpuTopology? topology = null,
        MachineKind machineKind = MachineKind.Desktop,
        IPrivilegeContext? privileges = null)
    {
        CpuTopology cpu = topology ?? MachineFixtures.IntelDesktopEightCore();
        SystemProfile profile = MachineFixtures.ProfileFor(cpu, machineKind);
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(cpu);

        return CompatibilityEvaluator.Evaluate(
            descriptor,
            profile,
            layout,
            privileges ?? new FakePrivilegeContext());
    }

    private static TweakDescriptor Descriptor() => new()
    {
        Id = "test.descriptor",
        Name = "Test",
        Category = TweakCategory.Cpu,
        Summary = "Summary.",
        TechnicalDescription = "Technical.",
        ExpectedEffect = "None.",
        Risk = RiskLevel.Safe,
        Scope = TweakScope.Session,
        RequiresElevation = false,
        RequiresRestart = false,
        BenchmarkRecommended = false,
    };
}

/// <summary>
/// The fingerprint scopes every stored measurement. If it were unstable, benchmark history would
/// silently fragment; if it were too coarse, results from a different driver would be compared as
/// if they were evidence.
/// </summary>
public sealed class HardwareFingerprintTests
{
    [Fact]
    public void SameMachine_ProducesTheSameFingerprint()
    {
        SystemProfile first = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());
        SystemProfile second = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        Assert.True(first.Fingerprint.IsComparableTo(second.Fingerprint));
    }

    [Fact]
    public void DifferentProcessor_ProducesADifferentFingerprint()
    {
        SystemProfile intel = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());
        SystemProfile amd = MachineFixtures.ProfileFor(MachineFixtures.AmdSingleChiplet());

        Assert.False(intel.Fingerprint.IsComparableTo(amd.Fingerprint));
        Assert.NotEqual(intel.Fingerprint.CpuHash, amd.Fingerprint.CpuHash);
    }

    [Fact]
    public void DifferentWindowsBuild_ProducesADifferentFingerprint()
    {
        SystemProfile before = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());
        SystemProfile after = MachineFixtures.ProfileFor(
            MachineFixtures.IntelDesktopEightCore(), buildNumber: 27000);

        Assert.NotEqual(before.Fingerprint.OsHash, after.Fingerprint.OsHash);
        Assert.False(before.Fingerprint.IsComparableTo(after.Fingerprint));
    }

    [Fact]
    public void DifferentDriverVersion_ProducesADifferentFingerprint()
    {
        CpuTopology cpu = MachineFixtures.IntelDesktopEightCore();
        SystemProfile before = MachineFixtures.ProfileFor(cpu);
        SystemProfile after = MachineFixtures.ProfileFor(cpu, gpus: new[]
        {
            new GpuDevice
            {
                DeviceInstanceId = @"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000&REV_A1\4&1234&0&0008",
                Description = "NVIDIA GeForce RTX 4090",
                Vendor = GpuVendor.Nvidia,
                DriverVersion = "32.0.16.0000",
            },
        });

        // A driver update invalidates earlier measurements, so it must change the key they are
        // stored under.
        Assert.NotEqual(before.Fingerprint.GpuHash, after.Fingerprint.GpuHash);
    }

    [Fact]
    public void AdapterEnumerationOrder_DoesNotChangeTheFingerprint()
    {
        CpuTopology cpu = MachineFixtures.IntelDesktopEightCore();

        var discrete = new GpuDevice
        {
            DeviceInstanceId = @"PCI\VEN_10DE&DEV_2684",
            Description = "NVIDIA GeForce RTX 4090",
            Vendor = GpuVendor.Nvidia,
            DriverVersion = "32.0.15.6094",
        };

        var integrated = new GpuDevice
        {
            DeviceInstanceId = @"PCI\VEN_8086&DEV_A780",
            Description = "Intel UHD Graphics 770",
            Vendor = GpuVendor.Intel,
            DriverVersion = "31.0.101.5186",
        };

        SystemProfile one = MachineFixtures.ProfileFor(cpu, gpus: new[] { discrete, integrated });
        SystemProfile two = MachineFixtures.ProfileFor(cpu, gpus: new[] { integrated, discrete });

        Assert.Equal(one.Fingerprint.CompositeHash, two.Fingerprint.CompositeHash);
    }

    [Fact]
    public void Fingerprint_ContainsNoIdentifyingInformation()
    {
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        // Hashes are hex only: nothing recoverable travels with a result.
        Assert.Matches("^[0-9a-f]{32}$", profile.Fingerprint.CompositeHash);
        Assert.Equal(12, profile.Fingerprint.ShortId.Length);
    }
}
