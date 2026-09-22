using System;
using System.Collections.Generic;
using System.Linq;

namespace Velocity.Ipc.Authorization;

/// <summary>Outcome of an authorization check.</summary>
/// <param name="Allowed">Whether the operation may proceed.</param>
/// <param name="Reason">Why it was allowed or refused, recorded in the audit log.</param>
public readonly record struct PolicyDecision(bool Allowed, string Reason)
{
    /// <summary>Creates an allowing decision.</summary>
    /// <param name="reason">Rule that permitted the operation.</param>
    /// <returns>The decision.</returns>
    public static PolicyDecision Allow(string reason) => new(true, reason);

    /// <summary>Creates a refusing decision.</summary>
    /// <param name="reason">Rule that refused the operation.</param>
    /// <returns>The decision.</returns>
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The policy that decides what the privileged helper is willing to change.
/// </summary>
/// <remarks>
/// <para>
/// This is the security boundary of the product. The helper runs as SYSTEM; without this class,
/// compromising or impersonating the desktop process would mean arbitrary SYSTEM level registry
/// writes. Every privileged write passes through here, on the helper side, where a compromised
/// caller cannot bypass it.
/// </para>
/// <para>
/// The design is allow-list first: a registry path is refused unless it matches a prefix that a
/// real optimization module needs. A deny list is layered on top of that for paths and value names
/// that fall inside an allowed prefix but must never be written, which is how the mitigation
/// settings inside Memory Management and the debugger hijack inside Image File Execution Options
/// are kept out of reach.
/// </para>
/// <para>
/// Nothing in this policy weakens security for performance. Defender, the firewall, BitLocker,
/// Secure Boot, Device Guard, LSA, Windows Update and the speculative execution mitigations are
/// refused outright. Their performance impact is reported in the UI with its security consequence;
/// changing them is the user's decision to make in Windows, not this product's to make silently.
/// </para>
/// </remarks>
public static class PrivilegedOperationPolicy
{
    /// <summary>Registry hives the helper will touch at all.</summary>
    private static readonly string[] AllowedHives = ["HKLM", "HKCU"];

    /// <summary>
    /// Key prefixes a privileged write may target. Comparison is ordinal, case insensitive, on
    /// <c>HIVE\path</c> with backslash separators.
    /// </summary>
    private static readonly (string Prefix, string Purpose)[] WriteAllowList =
    [
        (@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl",
            "foreground/background quantum split used by the scheduler module"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "GPU scheduling and presentation settings"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings",
            "power setting attribute visibility"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "paging and memory management behaviour"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            "TCP/IP stack parameters used by the network module"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}",
            "network adapter miniport keywords"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "multimedia class scheduler service settings"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
            "per executable performance options"),
        (@"HKCU\System\GameConfigStore",
            "per user game configuration"),
        (@"HKCU\Software\Microsoft\GameBar",
            "Game Bar behaviour"),
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "background game recording"),
    ];

    /// <summary>
    /// Paths that are never written, checked before the allow list. Several of these sit inside an
    /// allowed prefix, which is exactly why the deny list exists.
    /// </summary>
    private static readonly (string Prefix, string Reason)[] DenyList =
    [
        (@"HKLM\SECURITY", "security hive"),
        (@"HKLM\SAM", "account database"),
        (@"HKLM\SOFTWARE\Microsoft\Windows Defender", "Microsoft Defender configuration"),
        (@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "Microsoft Defender policy"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate", "Windows Update configuration"),
        (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "Windows Update policy"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "startup persistence"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "startup persistence"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "logon shell configuration"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot", "Secure Boot state"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "virtualization based security"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Lsa", "Local Security Authority"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\BitLocker", "BitLocker"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\CI", "code integrity"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server", "remote desktop configuration"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Microsoft Defender service"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\SecurityHealthService", "Windows Security service"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\wscsvc", "Security Center service"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\MpsSvc", "Windows Firewall service"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\BFE", "Base Filtering Engine"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess", "firewall configuration"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\TrustedInstaller", "component servicing"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\CryptSvc", "cryptographic services"),
        (@"HKLM\SYSTEM\CurrentControlSet\Policies", "machine policy"),
    ];

    /// <summary>
    /// Value names that are refused inside an otherwise allowed key, with the reason. Keyed by the
    /// allowed prefix they apply to.
    /// </summary>
    private static readonly (string KeyPrefix, string ValueName, string Reason)[] DeniedValues =
    [
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "FeatureSettings",
            "speculative execution mitigation control"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "FeatureSettingsOverride",
            "speculative execution mitigation control"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "FeatureSettingsOverrideMask",
            "speculative execution mitigation control"),
    ];

    /// <summary>
    /// Value names permitted under Image File Execution Options. Everything else there, above all
    /// <c>Debugger</c>, is refused: that key is a well known process hijack primitive.
    /// </summary>
    private static readonly string[] ImageFileExecutionOptionsAllowedValues =
    [
        "CpuPriorityClass",
        "IoPriority",
        "PagePriority",
        "WorkingSetLimitInKB",
    ];

    private const string ImageFileExecutionOptionsPrefix =
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>Decides whether a privileged registry write may proceed.</summary>
    /// <param name="hive">Hive short name, for example <c>HKLM</c>.</param>
    /// <param name="keyPath">Key path inside the hive, without a leading separator.</param>
    /// <param name="valueName">Value name, or <see langword="null"/> for the default value.</param>
    /// <returns>The decision.</returns>
    public static PolicyDecision AuthorizeRegistryWrite(string hive, string keyPath, string? valueName)
    {
        if (string.IsNullOrWhiteSpace(hive) || string.IsNullOrWhiteSpace(keyPath))
        {
            return PolicyDecision.Deny("Hive and key path are required.");
        }

        if (!AllowedHives.Contains(hive, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny($"Hive '{hive}' is not writable by the helper.");
        }

        if (ContainsPathTraversal(keyPath))
        {
            return PolicyDecision.Deny("Key path contains relative segments.");
        }

        string fullPath = $"{hive.ToUpperInvariant()}\\{keyPath.Trim('\\')}";

        foreach ((string prefix, string reason) in DenyList)
        {
            if (IsUnder(fullPath, prefix))
            {
                return PolicyDecision.Deny($"'{prefix}' is protected ({reason}) and is never written.");
            }
        }

        foreach ((string keyPrefix, string deniedValue, string reason) in DeniedValues)
        {
            if (IsUnder(fullPath, keyPrefix) &&
                string.Equals(valueName, deniedValue, StringComparison.OrdinalIgnoreCase))
            {
                return PolicyDecision.Deny($"Value '{deniedValue}' is protected ({reason}).");
            }
        }

        if (IsUnder(fullPath, ImageFileExecutionOptionsPrefix))
        {
            return AuthorizeImageFileExecutionOptions(fullPath, valueName);
        }

        foreach ((string prefix, string purpose) in WriteAllowList)
        {
            if (IsUnder(fullPath, prefix))
            {
                return PolicyDecision.Allow($"Permitted by the allow list entry for {purpose}.");
            }
        }

        return PolicyDecision.Deny(
            $"'{fullPath}' is not in the privileged write allow list. Add an allow list entry with a " +
            "documented purpose before a module can write it.");
    }

    /// <summary>Decides whether a privileged registry read may proceed.</summary>
    /// <param name="hive">Hive short name.</param>
    /// <param name="keyPath">Key path inside the hive.</param>
    /// <returns>The decision.</returns>
    /// <remarks>
    /// Reads are broader than writes because detection has to describe the machine honestly,
    /// including settings the product will never change. The two hives holding credential material
    /// are still refused.
    /// </remarks>
    public static PolicyDecision AuthorizeRegistryRead(string hive, string keyPath)
    {
        if (string.IsNullOrWhiteSpace(hive) || string.IsNullOrWhiteSpace(keyPath))
        {
            return PolicyDecision.Deny("Hive and key path are required.");
        }

        if (ContainsPathTraversal(keyPath))
        {
            return PolicyDecision.Deny("Key path contains relative segments.");
        }

        string fullPath = $"{hive.ToUpperInvariant()}\\{keyPath.Trim('\\')}";

        if (IsUnder(fullPath, @"HKLM\SAM") || IsUnder(fullPath, @"HKLM\SECURITY"))
        {
            return PolicyDecision.Deny("Credential hives are never read.");
        }

        return PolicyDecision.Allow("Reads are permitted outside the credential hives.");
    }

    /// <summary>The documented write allow list, exposed for the Expert Mode policy viewer.</summary>
    /// <returns>Each permitted prefix with the purpose that justifies it.</returns>
    public static IReadOnlyList<(string Prefix, string Purpose)> DescribeWriteAllowList() => WriteAllowList;

    /// <summary>The documented deny list, exposed for the Expert Mode policy viewer.</summary>
    /// <returns>Each protected prefix with the reason it is protected.</returns>
    public static IReadOnlyList<(string Prefix, string Reason)> DescribeDenyList() => DenyList;

    private static PolicyDecision AuthorizeImageFileExecutionOptions(string fullPath, string? valueName)
    {
        // Only the PerfOptions subkey is reachable, and only for the four performance values.
        if (!fullPath.EndsWith(@"\PerfOptions", StringComparison.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny(
                "Only the PerfOptions subkey of Image File Execution Options may be written; the key " +
                "itself allows process hijacking.");
        }

        if (valueName is null ||
            !ImageFileExecutionOptionsAllowedValues.Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny(
                $"Only {string.Join(", ", ImageFileExecutionOptionsAllowedValues)} may be written under PerfOptions.");
        }

        return PolicyDecision.Allow("Permitted per executable performance option.");
    }

    private static bool ContainsPathTraversal(string keyPath) =>
        keyPath.Contains("..", StringComparison.Ordinal) ||
        keyPath.Contains('/', StringComparison.Ordinal);

    private static bool IsUnder(string fullPath, string prefix) =>
        fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
}
