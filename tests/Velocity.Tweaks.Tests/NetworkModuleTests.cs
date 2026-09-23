using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.State;
using Velocity.TestSupport;
using Velocity.Tweaks;
using Velocity.Tweaks.Network;

namespace Velocity.Tweaks.Tests;

/// <summary>
/// The rule these modules exist to enforce: a keyword the driver does not publish is not a setting.
/// </summary>
public sealed class NetworkModuleTests
{
    private const string DriverPath =
        @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0012";

    [Fact]
    public async Task ADriverThatDoesNotPublishTheKeyword_MakesTheModuleUnsupported()
    {
        TweakContext context = Context(out _, keywords: new Dictionary<string, string>());

        CompatibilityResult result = await new EnergyEfficientEthernetTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
        Assert.Contains("does not publish", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriverThatDoesNotPublishTheKeyword_IsNeverWrittenTo()
    {
        TweakContext context = Context(out InMemoryStateProvider provider,
            keywords: new Dictionary<string, string>());

        ApplyResult result = await new EnergyEfficientEthernetTweak()
            .ApplyAsync(context, CancellationToken.None);

        // Creating the value would look like success and do nothing.
        Assert.Equal(ApplyOutcome.NoChangeRequired, result.Outcome);
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task ADriverThatPublishesTheKeyword_IsSupported()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(RegistryKeys.Value(DriverPath, "*EEE"), StateValue.FromString("1"));

        CompatibilityResult result = await new EnergyEfficientEthernetTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.True(result.IsSupported);
    }

    [Fact]
    public async Task AnAlreadyConfiguredAdapter_IsReportedAsOptimalRatherThanRewritten()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(RegistryKeys.Value(DriverPath, "*EEE"), StateValue.FromString("0"));

        CompatibilityResult result = await new EnergyEfficientEthernetTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.AlreadyOptimal, result.Status);
    }

    [Fact]
    public async Task Apply_WritesTheKeywordAndWarnsThatTheLinkDrops()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(RegistryKeys.Value(DriverPath, "*EEE"), StateValue.FromString("1"));

        ApplyResult result = await new EnergyEfficientEthernetTweak()
            .ApplyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("link drops", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            StateValue.FromString("0"),
            await provider.ReadAsync(RegistryKeys.Value(DriverPath, "*EEE"), CancellationToken.None));
    }

    [Fact]
    public async Task Verify_FailsWhenTheDriverRejectedTheValue()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(RegistryKeys.Value(DriverPath, "*EEE"), StateValue.FromString("1"));

        VerificationResult result = await new EnergyEfficientEthernetTweak()
            .VerifyAsync(context, CancellationToken.None);

        Assert.Equal(VerificationStatus.Mismatch, result.Status);
    }

    [Fact]
    public async Task WithNoAdapterCarryingTheDefaultRoute_NothingIsOffered()
    {
        TweakContext context = Context(out _, carriesDefaultRoute: false);

        CompatibilityResult result = await new AdapterPowerManagementTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
        Assert.Empty(await new AdapterPowerManagementTweak()
            .GetStateKeysAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task ThePowerManagementModuleWritesTheDocumentedValue()
    {
        TweakContext context = Context(
            out InMemoryStateProvider provider,
            keywords: new Dictionary<string, string> { ["PnPCapabilities"] = "0" });

        provider.Seed(RegistryKeys.Value(DriverPath, "PnPCapabilities"), StateValue.FromString("0"));

        await new AdapterPowerManagementTweak().ApplyAsync(context, CancellationToken.None);

        Assert.Equal(
            StateValue.FromString("24"),
            await provider.ReadAsync(
                RegistryKeys.Value(DriverPath, "PnPCapabilities"), CancellationToken.None));
    }

    [Fact]
    public void TheInterruptModerationModuleStatesTheTradeItMakes()
    {
        TweakDescriptor descriptor = new InterruptModerationTweak().Descriptor;

        Assert.True(descriptor.BenchmarkRecommended);
        Assert.Contains("higher CPU usage", descriptor.ExpectedEffect, StringComparison.Ordinal);
        Assert.Contains("net loss on the wrong machine", descriptor.ExpectedEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNetworkModuleClaimsToReducePing()
    {
        foreach (NetworkKeywordTweak module in new NetworkKeywordTweak[]
                 {
                     new AdapterPowerManagementTweak(),
                     new EnergyEfficientEthernetTweak(),
                     new InterruptModerationTweak(),
                 })
        {
            string text = module.Descriptor.ExpectedEffect + module.Descriptor.Summary;
            Assert.DoesNotContain("reduces ping", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lower ping", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static TweakContext Context(
        out InMemoryStateProvider provider,
        Dictionary<string, string>? keywords = null,
        bool carriesDefaultRoute = true)
    {
        provider = new InMemoryStateProvider(RegistryKeys.Scheme);

        var adapter = new NetworkAdapter
        {
            InterfaceId = "{adapter-guid}",
            Name = "Ethernet",
            Description = "Test Gigabit Adapter",
            Kind = NetworkInterfaceKind.Ethernet,
            IsUp = true,
            CarriesDefaultRoute = carriesDefaultRoute,
            LinkSpeedBitsPerSecond = 1_000_000_000,
            DriverRegistryPath = DriverPath,
            Capabilities = new NetworkAdapterCapabilities
            {
                AdvancedProperties = keywords ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["*EEE"] = "1",
                    ["*InterruptModeration"] = "1",
                    ["PnPCapabilities"] = "0",
                },
            },
        };

        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore()) with
        {
            NetworkAdapters = new[] { adapter },
        };

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            "network.test",
            new[]
            {
                RegistryKeys.Value(DriverPath, "*EEE"),
                RegistryKeys.Value(DriverPath, "*InterruptModeration"),
                RegistryKeys.Value(DriverPath, "PnPCapabilities"),
            });

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance);
    }
}
