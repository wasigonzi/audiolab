using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Velocity.Abstractions.Processes;

namespace Velocity.Core.Processes;

/// <summary>
/// Decides how far the optimizer may go with a given process.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>safety</em> list, which is the opposite of the "disable these fifty services"
/// lists this product refuses to ship. Nothing here says a process should be interfered with; it
/// says which processes must not be, and why.
/// </para>
/// <para>
/// The default for an unrecognised process is <see cref="ProcessProtection.None"/> only for
/// ordinary user software. Anything running as a system account, anything whose image sits in the
/// Windows directory, and anything that cannot be identified is protected, because the cost of
/// wrongly suspending a security or platform process is far higher than the frames gained by
/// de-prioritising one more background application.
/// </para>
/// </remarks>
public static class ProcessProtectionClassifier
{
    /// <summary>
    /// Processes that are never touched: the session, the shell, security software and anything
    /// whose interruption could cost the user data or protection.
    /// </summary>
    private static readonly HashSet<string> NeverTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        // Session and kernel surface.
        "system", "registry", "memory compression", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "fontdrvhost.exe",
        "dwm.exe", "sihost.exe", "ctfmon.exe", "logonui.exe", "consent.exe", "wudfhost.exe",

        // Microsoft security.
        "msmpeng.exe", "mpdefendercoreservice.exe", "nissrv.exe", "securityhealthservice.exe",
        "securityhealthsystray.exe", "smartscreen.exe", "mpcmdrun.exe",

        // Third party security agents. Suspending or de-prioritising one of these can trip its own
        // tamper protection and is never worth a frame.
        "avp.exe", "avgui.exe", "avastui.exe", "bdagent.exe", "ekrn.exe", "mcshield.exe",
        "nortonsecurity.exe", "sentinelagent.exe", "csfalconservice.exe", "cb.exe", "xagt.exe",

        // Anti-cheat. Interfering with these gets users banned.
        "easyanticheat.exe", "easyanticheat_eos.exe", "battleye.exe", "beservice.exe",
        "vgtray.exe", "vgc.exe", "faceit.exe", "esea.exe", "gameguard.des",
    };

    /// <summary>
    /// Processes that may be de-prioritised or confined but never suspended, because freezing them
    /// produces an immediately visible fault.
    /// </summary>
    private static readonly HashSet<string> NeverSuspend = new(StringComparer.OrdinalIgnoreCase)
    {
        // Audio: suspending the engine produces a stall the user hears instantly.
        "audiodg.exe", "rtkngui64.exe", "nahimicservice.exe", "voicemeeter.exe", "equalizerapo.exe",

        // Shell: suspending it freezes the desktop and the alt-tab list.
        "explorer.exe", "startmenuexperiencehost.exe", "searchhost.exe", "shellexperiencehost.exe",

        // Input and peripheral stacks.
        "ghub.exe", "lghub.exe", "icue.exe", "synapse3.exe", "razer synapse service.exe",
        "steelseriesengine.exe", "wootility.exe",

        // Overlays that inject into the game. Suspending a process the game has loaded a hook from
        // can hang or crash the game itself.
        "discord.exe", "steamoverlayui.exe", "gameoverlayui.exe", "nvidia share.exe",
        "nvcontainer.exe", "rtsshooks64.exe", "rtss.exe", "msiafterburner.exe",
    };

    /// <summary>Classifies a process.</summary>
    /// <param name="executableName">Executable name, with or without a path.</param>
    /// <param name="executablePath">Full image path, when known.</param>
    /// <param name="isSystemProcess">Whether the process runs under a system account.</param>
    /// <returns>How far the optimizer may go.</returns>
    public static ProcessProtection Classify(
        string? executableName,
        string? executablePath = null,
        bool isSystemProcess = false)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            // Something we cannot identify is something we do not touch.
            return ProcessProtection.Protected;
        }

        string name = GetFileName(executableName);

        if (NeverTouch.Contains(name))
        {
            return ProcessProtection.Protected;
        }

        if (isSystemProcess)
        {
            return ProcessProtection.Protected;
        }

        if (executablePath is not null && IsUnderWindowsDirectory(executablePath))
        {
            return ProcessProtection.Protected;
        }

        return NeverSuspend.Contains(name) ? ProcessProtection.NeverSuspend : ProcessProtection.None;
    }

    /// <summary>Classifies a snapshot, preserving everything else about it.</summary>
    /// <param name="snapshot">Process to classify.</param>
    /// <returns>The same snapshot with <see cref="ProcessSnapshot.Protection"/> filled in.</returns>
    public static ProcessSnapshot Classify(ProcessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot with
        {
            Protection = Classify(
                snapshot.ExecutableName, snapshot.ExecutablePath, snapshot.IsSystemProcess),
        };
    }

    /// <summary>The protected names, exposed so Expert Mode can show exactly what is off limits.</summary>
    /// <returns>Executable names that are never touched.</returns>
    public static IReadOnlyCollection<string> DescribeNeverTouched() => NeverTouch.Order().ToList();

    /// <summary>The never-suspend names, exposed for the same reason.</summary>
    /// <returns>Executable names that may be de-prioritised but never suspended.</returns>
    public static IReadOnlyCollection<string> DescribeNeverSuspended() => NeverSuspend.Order().ToList();

    /// <summary>
    /// Extracts the file name from a Windows path.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> is not used because it splits on the <em>host</em>
    /// platform's separator. The paths handed to this classifier always come from Windows, and the
    /// classifier itself runs on any platform during testing, so the separators are handled
    /// explicitly rather than depending on where the code happens to be executing.
    /// </remarks>
    private static string GetFileName(string executableName)
    {
        string trimmed = executableName.Trim();
        int separator = trimmed.LastIndexOfAny(['\\', '/']);

        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }

    private static bool IsUnderWindowsDirectory(string executablePath)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // On a non-Windows build agent the folder resolves to an empty string; treat that as
        // "cannot tell" rather than as "everything matches".
        return !string.IsNullOrEmpty(windows) &&
               executablePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }
}
