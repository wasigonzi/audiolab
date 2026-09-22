using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;
using Velocity.Core.Advisory;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers the analyzer that reports what the product cannot fix.
/// </summary>
/// <remarks>
/// These tests exist because the advisor is where the product's honesty is most load bearing: it is
/// the component that tells a user their 240 Hz monitor is running at 60 Hz instead of selling them
/// a registry change.
/// </remarks>
public sealed class SystemAdvisorTests
{
    private static MemoryInfo Memory(
        long total = 32L * 1024 * 1024 * 1024,
        long available = 18L * 1024 * 1024 * 1024,
        long commit = 20L * 1024 * 1024 * 1024,
        long commitLimit = 48L * 1024 * 1024 * 1024,
        IReadOnlyList<MemoryModule>? modules = null) => new()
    {
        TotalPhysicalBytes = total,
        AvailablePhysicalBytes = available,
        CommitTotalBytes = commit,
        CommitLimitBytes = commitLimit,
        PageSizeBytes = 4096,
        Modules = modules ?? [],
    };

    private static MemoryModule Module(string slot, int speed) => new()
    {
        Slot = slot,
        CapacityBytes = 16L * 1024 * 1024 * 1024,
        ConfiguredSpeedMtps = speed,
    };

    [Fact]
    public void HealthyMemoryProducesNoFindings()
    {
        Assert.Empty(SystemAdvisor.AnalyzeMemory(
            Memory(modules: [Module("DIMM 0", 6000), Module("DIMM 1", 6000)])));
    }

    [Fact]
    public void CommitChargeNearTheLimitIsAWarning()
    {
        IReadOnlyList<SystemFinding> findings = SystemAdvisor.AnalyzeMemory(
            Memory(commit: 46L * 1024 * 1024 * 1024, commitLimit: 48L * 1024 * 1024 * 1024));

        SystemFinding finding = Assert.Single(findings, f => f.Id == "memory.commit-pressure");
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("Commit charge is", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommitLimitIsNotReportedAsPressure()
    {
        // A probe that could not read the limit must not produce a division by zero warning.
        Assert.DoesNotContain(
            SystemAdvisor.AnalyzeMemory(Memory(commit: 40L * 1024 * 1024 * 1024, commitLimit: 0)),
            finding => finding.Id == "memory.commit-pressure");
    }

    [Fact]
    public void LowAvailableMemorySaysItWillNotEmptyWorkingSets()
    {
        IReadOnlyList<SystemFinding> findings = SystemAdvisor.AnalyzeMemory(
            Memory(available: 1L * 1024 * 1024 * 1024));

        SystemFinding finding = Assert.Single(findings, f => f.Id == "memory.low-available");
        Assert.Contains("does not empty working sets", finding.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleModuleIsReportedAsSingleChannel()
    {
        Assert.Contains(
            SystemAdvisor.AnalyzeMemory(Memory(modules: [Module("DIMM 0", 6000)])),
            finding => finding.Id == "memory.single-channel");
    }

    [Fact]
    public void MismatchedModuleSpeedsAreReported()
    {
        IReadOnlyList<SystemFinding> findings = SystemAdvisor.AnalyzeMemory(
            Memory(modules: [Module("DIMM 0", 6000), Module("DIMM 1", 4800)]));

        SystemFinding finding = Assert.Single(findings, f => f.Id == "memory.mismatched-speed");
        Assert.Contains("4800, 6000", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulesWithAnUnreadableSpeedAreNotCalledMismatched()
    {
        Assert.DoesNotContain(
            SystemAdvisor.AnalyzeMemory(Memory(modules: [Module("DIMM 0", 6000), Module("DIMM 1", 0)])),
            finding => finding.Id == "memory.mismatched-speed");
    }

    [Fact]
    public void ProtectedProcessesAreNeverNamedAsHeavyBackgroundConsumers()
    {
        var processes = new List<ProcessSnapshot>
        {
            new()
            {
                ProcessId = 4,
                ExecutableName = "MsMpEng.exe",
                WorkingSetBytes = 8L * 1024 * 1024 * 1024,
                Protection = ProcessProtection.Protected,
            },
            new()
            {
                ProcessId = 100,
                ExecutableName = "chrome.exe",
                WorkingSetBytes = 3L * 1024 * 1024 * 1024,
                Protection = ProcessProtection.None,
            },
        };

        SystemFinding finding = Assert.Single(
            SystemAdvisor.AnalyzeMemory(Memory(), processes),
            f => f.Id == "memory.heavy-background");

        Assert.Contains("chrome.exe", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("MsMpEng.exe", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemOnAMechanicalDiskIsTheLoudestStorageFinding()
    {
        var device = new StorageDevice
        {
            DeviceId = @"\\.\PHYSICALDRIVE0",
            Model = "ST2000DM008",
            MediaType = StorageMediaType.HardDisk,
            BusType = StorageBusType.Sata,
            HostsSystemVolume = true,
            Volumes = [new StorageVolume { DriveLetter = "C:", TotalBytes = 2000, FreeBytes = 1800 }],
        };

        SystemFinding finding = Assert.Single(
            SystemAdvisor.AnalyzeStorage([device]),
            f => f.Id == "storage.system-on-hdd");

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("solid state drive", finding.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void ASolidStateSystemDiskProducesNoMediaFinding()
    {
        var device = new StorageDevice
        {
            DeviceId = @"\\.\PHYSICALDRIVE0",
            Model = "Samsung SSD 990 PRO 2TB",
            MediaType = StorageMediaType.SolidState,
            BusType = StorageBusType.Nvme,
            HostsSystemVolume = true,
            Volumes = [new StorageVolume { DriveLetter = "C:", TotalBytes = 2000, FreeBytes = 1800 }],
        };

        Assert.Empty(SystemAdvisor.AnalyzeStorage([device]));
    }

    [Fact]
    public void ANearlyFullVolumeIsReportedPerVolume()
    {
        var device = new StorageDevice
        {
            DeviceId = @"\\.\PHYSICALDRIVE1",
            Model = "WD_BLACK SN850X",
            MediaType = StorageMediaType.SolidState,
            BusType = StorageBusType.Nvme,
            Volumes =
            [
                new StorageVolume { DriveLetter = "D:", TotalBytes = 1000, FreeBytes = 950 },
                new StorageVolume { DriveLetter = "E:", TotalBytes = 1000, FreeBytes = 20 },
            ],
        };

        SystemFinding finding = Assert.Single(SystemAdvisor.AnalyzeStorage([device]));
        Assert.Equal("storage.low-free-space.E:", finding.Id);
    }

    [Fact]
    public void AVolumeWithAnUnreadableSizeIsSkipped()
    {
        var device = new StorageDevice
        {
            DeviceId = @"\\.\PHYSICALDRIVE1",
            Model = "Unknown",
            MediaType = StorageMediaType.SolidState,
            BusType = StorageBusType.Unknown,
            Volumes = [new StorageVolume { DriveLetter = "F:", TotalBytes = 0, FreeBytes = 0 }],
        };

        Assert.Empty(SystemAdvisor.AnalyzeStorage([device]));
    }

    [Fact]
    public void AMonitorRunningBelowItsMaximumRefreshRateIsReported()
    {
        var display = new DisplayDevice
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyName = "LG 27GP850",
            CurrentMode = new DisplayMode(2560, 1440, 60),
            MaximumRefreshRateHzAtCurrentResolution = 165,
            IsPrimary = true,
        };

        SystemFinding finding = Assert.Single(SystemAdvisor.AnalyzeDisplays([display]));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("165 Hz", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("60 Hz", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMonitorOneHertzBelowItsMaximumIsNotReported()
    {
        // 59.94 Hz reported as 59 against a 60 Hz maximum is a rounding artefact, not a defect.
        var display = new DisplayDevice
        {
            DeviceName = @"\\.\DISPLAY1",
            CurrentMode = new DisplayMode(1920, 1080, 59),
            MaximumRefreshRateHzAtCurrentResolution = 60,
        };

        Assert.Empty(SystemAdvisor.AnalyzeDisplays([display]));
    }

    [Fact]
    public void FindingsComeBackMostSevereFirst()
    {
        SystemProfile baseline = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        SystemProfile profile = baseline with
        {
            Memory = Memory(
                available: 1L * 1024 * 1024 * 1024,
                modules: [Module("DIMM 0", 6000)]),
            Displays =
            [
                new DisplayDevice
                {
                    DeviceName = @"\\.\DISPLAY1",
                    CurrentMode = new DisplayMode(2560, 1440, 60),
                    MaximumRefreshRateHzAtCurrentResolution = 165,
                },
            ],
        };

        IReadOnlyList<SystemFinding> findings = SystemAdvisor.Analyze(profile);

        Assert.True(findings.Count >= 3);
        Assert.Equal(
            findings.OrderByDescending(finding => finding.Severity).Select(finding => finding.Id),
            findings.Select(finding => finding.Id));
    }

    [Fact]
    public void ThereIsNoMemoryCleaningRecommendation()
    {
        // The product must never suggest emptying the standby list or working sets: the number
        // improves and the machine gets slower.
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore()) with
        {
            Memory = Memory(available: 512L * 1024 * 1024),
        };

        foreach (SystemFinding finding in SystemAdvisor.Analyze(profile))
        {
            string text = $"{finding.Title} {finding.Detail} {finding.Recommendation}";
            Assert.DoesNotContain("standby list", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("free up ram", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("clean memory", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
