using System;
using System.Collections.Generic;
using System.Linq;
using Velocity.Ipc.Authorization;

namespace Velocity.Ipc.Tests;

/// <summary>
/// The policy is the security boundary between an unelevated desktop process and a SYSTEM service.
/// Every rule that keeps a compromised caller from doing real damage has a test here.
/// </summary>
public sealed class PrivilegedOperationPolicyTests
{
    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpAckFrequency")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness")]
    public void PathsARealModuleNeeds_AreAllowed(string path, string valueName)
    {
        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite("HKLM", path, valueName);

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend", "Start")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\MpsSvc", "Start")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate", "DisableOSUpgrade")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\BitLocker", "PreventDeviceEncryption")]
    public void SecurityRelevantPaths_AreNeverWritten(string path, string valueName)
    {
        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite("HKLM", path, valueName);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("FeatureSettings")]
    [InlineData("FeatureSettingsOverride")]
    [InlineData("FeatureSettingsOverrideMask")]
    public void SpeculativeExecutionMitigations_AreRefusedInsideAnOtherwiseAllowedKey(string valueName)
    {
        // Memory Management is on the allow list; these three values inside it are not.
        PolicyDecision allowed = PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache");
        PolicyDecision refused = PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", valueName);

        Assert.True(allowed.Allowed);
        Assert.False(refused.Allowed);
        Assert.Contains("mitigation", refused.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageFileExecutionOptions_AllowsOnlyPerformanceValuesUnderPerfOptions()
    {
        const string PerfOptions =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\game.exe\PerfOptions";

        Assert.True(PrivilegedOperationPolicy
            .AuthorizeRegistryWrite("HKLM", PerfOptions, "CpuPriorityClass").Allowed);
        Assert.True(PrivilegedOperationPolicy
            .AuthorizeRegistryWrite("HKLM", PerfOptions, "IoPriority").Allowed);
        Assert.False(PrivilegedOperationPolicy
            .AuthorizeRegistryWrite("HKLM", PerfOptions, "Debugger").Allowed);
    }

    [Fact]
    public void ImageFileExecutionOptions_RefusesTheKeyItselfBecauseItAllowsProcessHijacking()
    {
        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\game.exe",
            "Debugger");

        Assert.False(decision.Allowed);
        Assert.Contains("PerfOptions", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupPersistenceKeys_AreRefused()
    {
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Velocity").Allowed);
    }

    [Fact]
    public void AnUnlistedPath_IsRefusedByDefault()
    {
        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM", @"SOFTWARE\SomeVendor\SomeProduct", "Setting");

        Assert.False(decision.Allowed);
        Assert.Contains("allow list", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathTraversal_IsRefused()
    {
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM",
            @"SYSTEM\CurrentControlSet\Control\PriorityControl\..\..\Services\WinDefend",
            "Start").Allowed);
    }

    [Fact]
    public void ForwardSlashes_AreRefusedRatherThanNormalised()
    {
        // Accepting them would mean two spellings of the same path and one of them unchecked.
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "HKLM", "SYSTEM/CurrentControlSet/Services/WinDefend", "Start").Allowed);
    }

    [Fact]
    public void AnUnsupportedHive_IsRefused()
    {
        Assert.False(PrivilegedOperationPolicy
            .AuthorizeRegistryWrite("HKEY_PERFORMANCE_DATA", "Anything", "Value").Allowed);
    }

    [Fact]
    public void CaseDoesNotChangeTheDecision()
    {
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryWrite(
            "hklm", @"system\currentcontrolset\services\windefend", "Start").Allowed);
    }

    [Fact]
    public void ThePerUserHive_IsWritableForGameSettings()
    {
        Assert.True(PrivilegedOperationPolicy
            .AuthorizeRegistryWrite("HKCU", @"System\GameConfigStore", "GameDVR_Enabled").Allowed);
    }

    [Fact]
    public void Reads_AreBroaderThanWritesButStopAtCredentialHives()
    {
        Assert.True(PrivilegedOperationPolicy
            .AuthorizeRegistryRead("HKLM", @"SOFTWARE\Microsoft\Windows Defender").Allowed);
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryRead("HKLM", "SAM").Allowed);
        Assert.False(PrivilegedOperationPolicy.AuthorizeRegistryRead("HKLM", @"SECURITY\Policy").Allowed);
    }

    [Fact]
    public void EveryAllowListEntryDocumentsItsPurpose()
    {
        IReadOnlyList<(string Prefix, string Purpose)> entries =
            PrivilegedOperationPolicy.DescribeWriteAllowList();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Prefix));
            Assert.False(string.IsNullOrWhiteSpace(entry.Purpose));
        });
    }

    [Fact]
    public void NoAllowListEntrySitsInsideADeniedPath()
    {
        IEnumerable<string> allowed = PrivilegedOperationPolicy
            .DescribeWriteAllowList().Select(entry => entry.Prefix);
        IReadOnlyList<(string Prefix, string Reason)> denied = PrivilegedOperationPolicy.DescribeDenyList();

        foreach (string prefix in allowed)
        {
            Assert.DoesNotContain(denied, entry =>
                prefix.StartsWith(entry.Prefix + "\\", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals(entry.Prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
    [Fact]
    public void ThePowerAllowListPermitsTheProcessorStateSettings()
    {
        Assert.True(PrivilegedOperationPolicy.AuthorizePowerSettingWrite(
            "54533251-82be-4824-96c1-47b60b740d00",
            "893dee8e-2bef-41e0-89c6-b55d0929964c").Allowed);

        Assert.True(PrivilegedOperationPolicy.AuthorizePowerSettingWrite(
            "54533251-82be-4824-96c1-47b60b740d00",
            "bc5038f7-23e0-4960-96da-33abaf5935ec").Allowed);
    }

    [Fact]
    public void AnUnlistedSettingInAnAllowedSubgroupIsStillRefused()
    {
        // The processor subgroup also holds the thermal throttle policy. Allow-listing by subgroup
        // would hand that over with the rest.
        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizePowerSettingWrite(
            "54533251-82be-4824-96c1-47b60b740d00",
            "68dd2f27-a4ce-4e11-8487-3794e4135dfa");

        Assert.False(decision.Allowed);
        Assert.Contains("not in the privileged write allow list", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APowerSettingWriteWithNonGuidArgumentsIsRefused()
    {
        Assert.False(PrivilegedOperationPolicy.AuthorizePowerSettingWrite("processor", "max").Allowed);
        Assert.False(PrivilegedOperationPolicy.AuthorizePowerSettingWrite(null, null).Allowed);
    }

    [Fact]
    public void AnyRealSchemeMayBeActivatedSoARestoreCanPutTheUsersPlanBack()
    {
        Assert.True(PrivilegedOperationPolicy
            .AuthorizePowerSchemeActivation("11111111-2222-3333-4444-555555555555").Allowed);
    }

    [Fact]
    public void AMalformedOrEmptySchemeGuidIsRefused()
    {
        Assert.False(PrivilegedOperationPolicy.AuthorizePowerSchemeActivation("high performance").Allowed);
        Assert.False(PrivilegedOperationPolicy
            .AuthorizePowerSchemeActivation("00000000-0000-0000-0000-000000000000").Allowed);
    }

    [Fact]
    public void EveryPowerAllowListEntryCarriesAPurpose()
    {
        Assert.NotEmpty(PrivilegedOperationPolicy.DescribePowerWriteAllowList());

        foreach ((string subgroup, string setting, string purpose) in
                 PrivilegedOperationPolicy.DescribePowerWriteAllowList())
        {
            Assert.True(Guid.TryParse(subgroup, out _));
            Assert.True(Guid.TryParse(setting, out _));
            Assert.False(string.IsNullOrWhiteSpace(purpose));
        }
    }

}
