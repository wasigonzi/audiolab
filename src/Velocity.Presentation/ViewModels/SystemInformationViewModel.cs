using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Core.Hardware;
using Velocity.Presentation.Mvvm;

namespace Velocity.Presentation.ViewModels;

/// <summary>One labelled fact about the machine.</summary>
/// <param name="Label">What the value describes.</param>
/// <param name="Value">The value, already formatted for display.</param>
/// <param name="Detail">Optional extra context shown in Expert Mode.</param>
public sealed record SystemFact(string Label, string Value, string? Detail = null);

/// <summary>A group of related facts.</summary>
/// <param name="Title">Group heading.</param>
/// <param name="Facts">Facts in the group.</param>
public sealed record SystemFactGroup(string Title, IReadOnlyList<SystemFact> Facts);

/// <summary>
/// Presents the detected hardware, including what could not be detected.
/// </summary>
/// <remarks>
/// Probe failures are shown rather than hidden. A user whose storage probe failed deserves to know
/// that the storage module is unavailable for that reason, instead of seeing an empty list and
/// concluding the product is broken or, worse, that they have no disks worth optimising.
/// </remarks>
public sealed partial class SystemInformationViewModel : ViewModelBase
{
    private readonly ISystemProfileProvider _profileProvider;

    /// <summary>Creates the view model.</summary>
    /// <param name="profileProvider">Source of the machine profile.</param>
    /// <param name="logger">Logger.</param>
    public SystemInformationViewModel(
        ISystemProfileProvider profileProvider,
        ILogger<SystemInformationViewModel> logger)
        : base(logger) =>
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));

    /// <summary>Fact groups shown on the page.</summary>
    public ObservableCollection<SystemFactGroup> Groups { get; } = new();

    /// <summary>Observations the topology analyzer made about this processor.</summary>
    public ObservableCollection<string> CpuNotes { get; } = new();

    /// <summary>Short form of the hardware fingerprint, shown so support can correlate reports.</summary>
    [ObservableProperty]
    public partial string FingerprintId { get; set; } = string.Empty;

    /// <summary>Probes that failed, with the reason.</summary>
    public ObservableCollection<string> ProbeFailures { get; } = new();

    /// <summary>Loads or reloads the machine profile.</summary>
    /// <param name="forceRefresh">Whether to re-probe rather than use the cached profile.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    [RelayCommand]
    public Task LoadAsync(bool forceRefresh) =>
        RunAsync(async token =>
        {
            SystemProfile profile = forceRefresh
                ? await _profileProvider.RefreshAsync(token).ConfigureAwait(true)
                : await _profileProvider.GetAsync(token).ConfigureAwait(true);

            CpuLayout layout = CpuTopologyAnalyzer.Analyze(profile.Cpu);
            Populate(profile, layout);
        },
        "Reading hardware information");

    private void Populate(SystemProfile profile, CpuLayout layout)
    {
        Groups.Clear();
        CpuNotes.Clear();
        ProbeFailures.Clear();

        FingerprintId = profile.Fingerprint.ShortId;

        Groups.Add(new SystemFactGroup("System", BuildSystemFacts(profile)));
        Groups.Add(new SystemFactGroup("Processor", BuildCpuFacts(profile, layout)));
        Groups.Add(new SystemFactGroup("Memory", BuildMemoryFacts(profile)));

        if (profile.Gpus.Count > 0)
        {
            Groups.Add(new SystemFactGroup("Graphics", BuildGpuFacts(profile)));
        }

        if (profile.Displays.Count > 0)
        {
            Groups.Add(new SystemFactGroup("Displays", BuildDisplayFacts(profile)));
        }

        if (profile.StorageDevices.Count > 0)
        {
            Groups.Add(new SystemFactGroup("Storage", BuildStorageFacts(profile)));
        }

        if (profile.NetworkAdapters.Count > 0)
        {
            Groups.Add(new SystemFactGroup("Network", BuildNetworkFacts(profile)));
        }

        Groups.Add(new SystemFactGroup("Power", BuildPowerFacts(profile)));

        foreach (string note in layout.Notes)
        {
            CpuNotes.Add(note);
        }

        foreach (KeyValuePair<string, string> failure in profile.ProbeFailures)
        {
            ProbeFailures.Add($"{failure.Key}: {failure.Value}");
        }
    }

    private static List<SystemFact> BuildSystemFacts(SystemProfile profile) =>
    [
        new("Operating system", profile.OperatingSystem.ProductName, profile.OperatingSystem.DisplayVersion),
        new("Build", string.Create(
            CultureInfo.InvariantCulture,
            $"{profile.OperatingSystem.BuildNumber}.{profile.OperatingSystem.UpdateBuildRevision}")),
        new("Architecture", profile.OperatingSystem.Architecture),
        new("Machine type", profile.MachineKind.ToString()),
        new("Uptime", FormatDuration(profile.OperatingSystem.Uptime)),
    ];

    private static List<SystemFact> BuildCpuFacts(SystemProfile profile, CpuLayout layout)
    {
        var facts = new List<SystemFact>
        {
            new("Model", profile.Cpu.BrandString),
            new("Cores", string.Create(
                CultureInfo.InvariantCulture,
                $"{profile.Cpu.PhysicalCoreCount} physical, {profile.Cpu.LogicalProcessorCount} logical")),
            new("Simultaneous multithreading",
                profile.Cpu.IsSimultaneousMultiThreadingEnabled ? "Enabled" : "Not present"),
        };

        if (profile.Cpu.IsHybrid)
        {
            facts.Add(new SystemFact(
                "Hybrid layout",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{layout.PerformanceCoreIds.Count} performance, {layout.EfficiencyCoreIds.Count} efficiency")));
        }

        if (layout.Complexes.Count > 1)
        {
            facts.Add(new SystemFact(
                "Core complexes",
                string.Create(CultureInfo.InvariantCulture, $"{layout.Complexes.Count}"),
                string.Join(
                    ", ",
                    layout.Complexes.Select(complex => string.Create(
                        CultureInfo.InvariantCulture,
                        $"#{complex.ComplexId}: {complex.PhysicalCoreIds.Count} cores / {complex.SharedCacheBytes / (1024 * 1024)} MB L{complex.SharedCacheLevel}")))));
        }

        if (profile.Cpu.Groups.Count > 1)
        {
            facts.Add(new SystemFact(
                "Processor groups",
                string.Create(CultureInfo.InvariantCulture, $"{profile.Cpu.Groups.Count}")));
        }

        return facts;
    }

    private static List<SystemFact> BuildMemoryFacts(SystemProfile profile)
    {
        var facts = new List<SystemFact>
        {
            new("Installed", FormatBytes(profile.Memory.TotalPhysicalBytes)),
            new("Available", FormatBytes(profile.Memory.AvailablePhysicalBytes)),
        };

        if (profile.Memory.CommitLimitBytes > 0)
        {
            facts.Add(new SystemFact(
                "Commit charge",
                $"{FormatBytes(profile.Memory.CommitTotalBytes)} of {FormatBytes(profile.Memory.CommitLimitBytes)}"));
        }

        foreach (MemoryModule module in profile.Memory.Modules)
        {
            facts.Add(new SystemFact(
                module.Slot,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FormatBytes(module.CapacityBytes)} at {module.ConfiguredSpeedMtps} MT/s"),
                module.PartNumber));
        }

        return facts;
    }

    private static List<SystemFact> BuildGpuFacts(SystemProfile profile) =>
        profile.Gpus.Select(gpu => new SystemFact(
            gpu.Description,
            gpu.DedicatedVideoMemoryBytes is long memory
                ? $"{FormatBytes(memory)}, driver {gpu.DriverVersion ?? "unknown"}"
                : $"Driver {gpu.DriverVersion ?? "unknown"}",
            $"Hardware scheduling: {DescribeScheduling(gpu.HardwareScheduling)}")).ToList();

    private static List<SystemFact> BuildDisplayFacts(SystemProfile profile) =>
        profile.Displays.Select(display => new SystemFact(
            display.FriendlyName ?? display.DeviceName,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{display.CurrentMode.Width}x{display.CurrentMode.Height} at {display.CurrentMode.RefreshRateHz} Hz"),
            display.IsRunningBelowMaximumRefreshRate
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"This monitor supports {display.MaximumRefreshRateHzAtCurrentResolution} Hz at this resolution.")
                : null)).ToList();

    private static List<SystemFact> BuildStorageFacts(SystemProfile profile) =>
        profile.StorageDevices.Select(device => new SystemFact(
            device.Model,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{FormatBytes(device.SizeBytes)} {device.MediaType} on {device.BusType}"),
            device.Volumes.Count == 0
                ? null
                : string.Join(", ", device.Volumes.Select(volume => volume.DriveLetter ?? "(no letter)")))).ToList();

    private static List<SystemFact> BuildNetworkFacts(SystemProfile profile) =>
        profile.NetworkAdapters
            .Where(adapter => adapter.IsUp)
            .Select(adapter => new SystemFact(
                adapter.Name,
                adapter.LinkSpeedBitsPerSecond > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{adapter.Kind}, {adapter.LinkSpeedBitsPerSecond / 1_000_000} Mb/s")
                    : adapter.Kind.ToString(),
                adapter.CarriesDefaultRoute ? "Carries the default route" : null))
            .ToList();

    private static List<SystemFact> BuildPowerFacts(SystemProfile profile)
    {
        var facts = new List<SystemFact>
        {
            new("Active plan", profile.Power.ActiveScheme.Name),
        };

        if (profile.Power.HasBattery)
        {
            facts.Add(new SystemFact("Power source", profile.Power.IsOnBattery ? "Battery" : "Mains"));
        }

        ProcessorPowerPolicy policy = profile.Power.ProcessorPolicy;

        if (policy.MinimumProcessorStatePercent is int minimum)
        {
            facts.Add(new SystemFact(
                "Minimum processor state",
                string.Create(CultureInfo.InvariantCulture, $"{minimum}%")));
        }

        if (policy.MaximumProcessorStatePercent is int maximum)
        {
            facts.Add(new SystemFact(
                "Maximum processor state",
                string.Create(CultureInfo.InvariantCulture, $"{maximum}%")));
        }

        facts.Add(new SystemFact(
            "Core parking",
            policy.CoreParkingExposed ? "Exposed by this platform" : "Not exposed by this platform"));

        return facts;
    }

    private static string DescribeScheduling(HardwareSchedulingState state) => state switch
    {
        HardwareSchedulingState.Enabled => "Enabled",
        HardwareSchedulingState.Disabled => "Disabled",
        HardwareSchedulingState.NotSupported => "Not supported",
        _ => "Unknown",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalDays}d {duration.Hours}h")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m");
}
