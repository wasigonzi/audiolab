using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;

namespace Velocity.Core.Advisory;

/// <summary>How much attention a finding deserves.</summary>
public enum FindingSeverity
{
    /// <summary>Worth knowing, nothing to do.</summary>
    Information = 0,

    /// <summary>Something the user could change, outside this product.</summary>
    Suggestion = 1,

    /// <summary>A configuration problem large enough that no tweak will compensate for it.</summary>
    Warning = 2,
}

/// <summary>One observation about the machine.</summary>
/// <param name="Id">Stable identifier, so the UI can suppress or link a finding.</param>
/// <param name="Title">One line summary.</param>
/// <param name="Detail">What was observed, with the numbers behind it.</param>
/// <param name="Severity">How much attention it deserves.</param>
/// <param name="Recommendation">What the user could do, when there is something.</param>
public sealed record SystemFinding(
    string Id,
    string Title,
    string Detail,
    FindingSeverity Severity,
    string? Recommendation = null);

/// <summary>
/// Produces findings about memory, storage and displays.
/// </summary>
/// <remarks>
/// <para>
/// This is where the product says the useful thing it cannot fix. A game installed on a mechanical
/// disk, a 240 Hz monitor running at 60 Hz, or a machine at its commit limit will dominate any
/// tweak in this product, and telling the user that is worth more than applying ten registry
/// changes around it.
/// </para>
/// <para>
/// <b>There is deliberately no memory optimization module.</b> Emptying working sets or the standby
/// list makes the "available memory" number go up and makes the machine slower, because the next
/// access has to fault the data back in. The honest thing to do about memory is to report it, so
/// that is all this does.
/// </para>
/// </remarks>
public static class SystemAdvisor
{
    /// <summary>Free space below this fraction of a drive is called out.</summary>
    public const double LowFreeSpaceFraction = 0.10d;

    /// <summary>Commit charge above this fraction of the limit is called out.</summary>
    public const double HighCommitFraction = 0.85d;

    /// <summary>Available memory below this fraction of installed memory is called out.</summary>
    public const double LowAvailableMemoryFraction = 0.10d;

    /// <summary>Produces every finding for a machine.</summary>
    /// <param name="profile">The machine.</param>
    /// <param name="processes">Running processes, when known, for memory attribution.</param>
    /// <returns>The findings, most severe first.</returns>
    public static IReadOnlyList<SystemFinding> Analyze(
        SystemProfile profile,
        IReadOnlyList<ProcessSnapshot>? processes = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var findings = new List<SystemFinding>();

        findings.AddRange(AnalyzeMemory(profile.Memory, processes));
        findings.AddRange(AnalyzeStorage(profile.StorageDevices));
        findings.AddRange(AnalyzeDisplays(profile.Displays));

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Produces the memory findings.</summary>
    /// <param name="memory">Memory state.</param>
    /// <param name="processes">Running processes, when known.</param>
    /// <returns>The findings.</returns>
    public static IReadOnlyList<SystemFinding> AnalyzeMemory(
        MemoryInfo memory,
        IReadOnlyList<ProcessSnapshot>? processes = null)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var findings = new List<SystemFinding>();

        if (memory.CommitLimitBytes > 0 &&
            (double)memory.CommitTotalBytes / memory.CommitLimitBytes > HighCommitFraction)
        {
            string charge = string.Create(
                CultureInfo.InvariantCulture,
                $"Commit charge is {Bytes(memory.CommitTotalBytes)} of {Bytes(memory.CommitLimitBytes)}.");

            findings.Add(new SystemFinding(
                "memory.commit-pressure",
                "This machine is close to its commit limit",
                charge +
                " Near the limit, Windows pages aggressively and games stutter regardless of how much " +
                "physical memory appears free.",
                FindingSeverity.Warning,
                "Close background applications, or allow a larger page file."));
        }

        if (memory.TotalPhysicalBytes > 0 &&
            (double)memory.AvailablePhysicalBytes / memory.TotalPhysicalBytes < LowAvailableMemoryFraction)
        {
            findings.Add(new SystemFinding(
                "memory.low-available",
                "Very little memory is available",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Bytes(memory.AvailablePhysicalBytes)} available of {Bytes(memory.TotalPhysicalBytes)} installed."),
                FindingSeverity.Warning,
                "Close what you are not using. This product does not empty working sets to make this " +
                "number look better, because doing so makes the machine slower."));
        }

        if (memory.Modules.Count == 1)
        {
            findings.Add(new SystemFinding(
                "memory.single-channel",
                "Only one memory module is installed",
                "A single module runs in single channel mode, roughly halving memory bandwidth. On " +
                "integrated graphics and on CPU bound games this costs more frames than any setting " +
                "in this product can recover.",
                FindingSeverity.Suggestion,
                "Fit a matched second module."));
        }

        if (memory.Modules.Count > 1)
        {
            int[] speeds = memory.Modules
                .Select(module => module.ConfiguredSpeedMtps)
                .Where(speed => speed > 0)
                .Distinct()
                .ToArray();

            if (speeds.Length > 1)
            {
                string configured = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Configured speeds: {string.Join(", ", speeds.Order())} MT/s.");

                findings.Add(new SystemFinding(
                    "memory.mismatched-speed",
                    "Memory modules are running at different speeds",
                    configured + " Mixed modules run at the slowest common configuration.",
                    FindingSeverity.Suggestion));
            }
        }

        if (processes is { Count: > 0 })
        {
            ProcessSnapshot[] heaviest = processes
                .Where(process => process.Protection == ProcessProtection.None)
                .OrderByDescending(process => process.WorkingSetBytes)
                .Take(3)
                .ToArray();

            if (heaviest.Length > 0 && heaviest[0].WorkingSetBytes > 1024L * 1024 * 1024)
            {
                findings.Add(new SystemFinding(
                    "memory.heavy-background",
                    "Background applications are holding a lot of memory",
                    string.Join(
                        ", ",
                        heaviest.Select(process => string.Create(
                            CultureInfo.InvariantCulture,
                            $"{process.ExecutableName} {Bytes(process.WorkingSetBytes)}"))),
                    FindingSeverity.Information));
            }
        }

        return findings;
    }

    /// <summary>Produces the storage findings.</summary>
    /// <param name="devices">Storage devices.</param>
    /// <returns>The findings.</returns>
    public static IReadOnlyList<SystemFinding> AnalyzeStorage(IReadOnlyList<StorageDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var findings = new List<SystemFinding>();

        foreach (StorageDevice device in devices.Where(device => device.HostsSystemVolume))
        {
            if (device.MediaType == StorageMediaType.HardDisk)
            {
                findings.Add(new SystemFinding(
                    "storage.system-on-hdd",
                    "Windows is installed on a mechanical disk",
                    $"{device.Model} is a rotational drive hosting the system volume. Every background " +
                    "read Windows performs competes with the game for seek time.",
                    FindingSeverity.Warning,
                    "Moving Windows to a solid state drive will do more for this machine than every " +
                    "setting in this product combined."));
            }
        }

        foreach (StorageDevice device in devices)
        {
            foreach (StorageVolume volume in device.Volumes.Where(volume => volume.TotalBytes > 0))
            {
                double free = (double)volume.FreeBytes / volume.TotalBytes;

                if (free < LowFreeSpaceFraction)
                {
                    string remaining = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Bytes(volume.FreeBytes)} free of {Bytes(volume.TotalBytes)} ({free:P0}).");

                    findings.Add(new SystemFinding(
                        $"storage.low-free-space.{volume.DriveLetter ?? device.DeviceId}",
                        $"{volume.DriveLetter ?? device.Model} is nearly full",
                        remaining +
                        " A nearly full solid state drive slows down as it runs out of free blocks to write to.",
                        FindingSeverity.Warning,
                        "Free some space on this drive."));
                }
            }
        }

        return findings;
    }

    /// <summary>Produces the display findings.</summary>
    /// <param name="displays">Attached monitors.</param>
    /// <returns>The findings.</returns>
    public static IReadOnlyList<SystemFinding> AnalyzeDisplays(IReadOnlyList<DisplayDevice> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        return displays
            .Where(display => display.IsRunningBelowMaximumRefreshRate)
            .Select(display => new SystemFinding(
                $"display.below-maximum-refresh.{display.DeviceName}",
                $"{display.FriendlyName ?? display.DeviceName} is not running at its highest refresh rate",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Currently {display.CurrentMode.RefreshRateHz} Hz at {display.CurrentMode.Width}x{display.CurrentMode.Height}, " +
                    $"but it supports {display.MaximumRefreshRateHzAtCurrentResolution} Hz at that resolution."),
                FindingSeverity.Warning,
                "Change it in Settings, System, Display, Advanced display. This is the single most " +
                "common configuration mistake on gaming PCs and no tweak compensates for it."))
            .ToList();
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = value;
        int unit = 0;

        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.#} {units[unit]}");
    }
}
