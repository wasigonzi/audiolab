using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Services;
using Velocity.Abstractions.State;
using Velocity.Core.Services;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// Classification decides what may be stopped. The dependency graph, not a shipped list, is what
/// has to make it correct on a machine nobody has seen.
/// </summary>
public sealed class ServiceClassifierTests
{
    [Fact]
    public void ADriver_IsNeverAService()
    {
        ServiceSnapshot result = Classify(
            ServiceFixtures.Service("nvlddmkm", kind: ServiceKind.KernelDriver));

        Assert.Equal(ServiceClassification.HardwareDependent, result.Classification);
    }

    [Theory]
    [InlineData(ServiceStartMode.Boot)]
    [InlineData(ServiceStartMode.System)]
    public void AKernelStartedService_IsCritical(ServiceStartMode startMode)
    {
        ServiceSnapshot result = Classify(
            ServiceFixtures.Service("something-early", startMode: startMode));

        Assert.Equal(ServiceClassification.Critical, result.Classification);
    }

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    [InlineData("PlugPlay")]
    [InlineData("AudioSrv")]
    [InlineData("Dnscache")]
    public void PlatformServices_AreCritical(string name)
    {
        Assert.Equal(ServiceClassification.Critical, Classify(ServiceFixtures.Service(name)).Classification);
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("MpsSvc")]
    [InlineData("BFE")]
    [InlineData("wuauserv")]
    [InlineData("BDESVC")]
    public void SecurityAndUpdateServices_AreNeverTouched(string name)
    {
        Assert.Equal(ServiceClassification.Security, Classify(ServiceFixtures.Service(name)).Classification);
    }

    [Theory]
    [InlineData("XblAuthManager")]
    [InlineData("GamingServices")]
    [InlineData("BEService")]
    [InlineData("vgc")]
    public void TheGamingStack_IsNeverTouched(string name)
    {
        Assert.Equal(
            ServiceClassification.GamingRelated, Classify(ServiceFixtures.Service(name)).Classification);
    }

    [Fact]
    public void AServiceWhoseDisplayNameMentionsAntiCheat_IsProtected()
    {
        ServiceSnapshot result = Classify(
            ServiceFixtures.Service("xyz123", displayName: "Some Game AntiCheat Service"));

        Assert.Equal(ServiceClassification.GamingRelated, result.Classification);
    }

    [Theory]
    [InlineData("NVDisplay.ContainerLocalSystem")]
    [InlineData("AMD External Events Utility")]
    [InlineData("RtkAudioUniversalService")]
    [InlineData("LGHUBUpdaterService")]
    public void HardwareVendorServices_AreProtected(string name)
    {
        Assert.Equal(
            ServiceClassification.HardwareDependent, Classify(ServiceFixtures.Service(name)).Classification);
    }

    [Fact]
    public void AServiceWithARunningDependent_IsRequired()
    {
        var provider = ServiceFixtures.Service("SomeProvider");
        var consumer = ServiceFixtures.Service("SomeConsumer", dependsOn: new[] { "SomeProvider" });

        ServiceSnapshot result = ServiceClassifier.Classify(provider, new[] { provider, consumer });

        Assert.Equal(ServiceClassification.Required, result.Classification);
        Assert.Contains("SomeConsumer", result.ClassificationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceWhoseDependentIsStopped_IsNotRequired()
    {
        var provider = ServiceFixtures.Service("SomeProvider");
        var consumer = ServiceFixtures.Service(
            "SomeConsumer", state: ServiceState.Stopped, dependsOn: new[] { "SomeProvider" });

        ServiceSnapshot result = ServiceClassifier.Classify(provider, new[] { provider, consumer });

        Assert.Equal(ServiceClassification.Optional, result.Classification);
    }

    [Fact]
    public void TheDeclaredDependentListIsAlsoConsulted()
    {
        var provider = ServiceFixtures.Service("SomeProvider", dependents: new[] { "SomeConsumer" });
        var consumer = ServiceFixtures.Service("SomeConsumer");

        ServiceSnapshot result = ServiceClassifier.Classify(provider, new[] { provider, consumer });

        Assert.Equal(ServiceClassification.Required, result.Classification);
    }

    [Fact]
    public void AStoppedService_OffersNothing()
    {
        Assert.Equal(
            ServiceClassification.AlreadyStopped,
            Classify(ServiceFixtures.Service("Idle", state: ServiceState.Stopped)).Classification);
    }

    [Fact]
    public void AnOrdinaryRunningServiceWithNoDependents_IsOptional()
    {
        ServiceSnapshot result = Classify(ServiceFixtures.Service("SomeVendorUpdater"));

        Assert.Equal(ServiceClassification.Optional, result.Classification);
        Assert.True(result.IsSessionCandidate);
    }

    [Fact]
    public void EveryClassificationExplainsItself()
    {
        IReadOnlyList<ServiceSnapshot> classified = ServiceClassifier.ClassifyAll(new[]
        {
            ServiceFixtures.Service("RpcSs"),
            ServiceFixtures.Service("WinDefend"),
            ServiceFixtures.Service("SomeUpdater"),
            ServiceFixtures.Service("Idle", state: ServiceState.Stopped),
        });

        Assert.All(classified, service => Assert.False(string.IsNullOrWhiteSpace(service.ClassificationReason)));
    }

    [Fact]
    public void TheProtectiveListsAreInspectable()
    {
        (IReadOnlyCollection<string> critical,
            IReadOnlyCollection<string> security,
            IReadOnlyCollection<string> gaming) = ServiceClassifier.DescribeProtectedServices();

        Assert.NotEmpty(critical);
        Assert.NotEmpty(security);
        Assert.NotEmpty(gaming);
    }

    private static ServiceSnapshot Classify(ServiceSnapshot service) =>
        ServiceClassifier.Classify(service, new[] { service });
}

/// <summary>The provider is where "never stop a protected service" is actually enforced.</summary>
public sealed class ServiceStateProviderTests
{
    [Fact]
    public async Task Read_ReportsTheRunningState()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("SomeUpdater"),
            ServiceFixtures.Service("Idle", state: ServiceState.Stopped));

        ServiceStateProvider provider = Create(inspector, out _);

        Assert.Equal("Running", (await provider.ReadAsync(Key("SomeUpdater"), CancellationToken.None)).Data);
        Assert.Equal("Stopped", (await provider.ReadAsync(Key("Idle"), CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Read_ReportsAbsentForAServiceThatDoesNotExist()
    {
        ServiceStateProvider provider = Create(new FakeServiceInspector(), out _);

        Assert.True((await provider.ReadAsync(Key("Nope"), CancellationToken.None)).IsAbsent);
    }

    [Fact]
    public async Task Write_StopsAnOptionalService()
    {
        var inspector = new FakeServiceInspector(ServiceFixtures.Service("SomeUpdater"));
        ServiceStateProvider provider = Create(inspector, out FakeServiceController controller);

        await provider.WriteAsync(
            Key("SomeUpdater"), StateValue.FromString("Stopped"), CancellationToken.None);

        Assert.Equal("SomeUpdater", Assert.Single(controller.Stopped));
    }

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("WinDefend")]
    [InlineData("BEService")]
    [InlineData("NVDisplay.ContainerLocalSystem")]
    public async Task Write_RefusesToStopAnythingProtected(string name)
    {
        var inspector = new FakeServiceInspector(ServiceFixtures.Service(name));
        ServiceStateProvider provider = Create(inspector, out FakeServiceController controller);

        await provider.WriteAsync(Key(name), StateValue.FromString("Stopped"), CancellationToken.None);

        // The refusal lives at the provider so no module can reach a protected service.
        Assert.Empty(controller.Stopped);
    }

    [Fact]
    public async Task Write_RefusesToStopAServiceSomethingDependsOn()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("SomeProvider"),
            ServiceFixtures.Service("SomeConsumer", dependsOn: new[] { "SomeProvider" }));

        ServiceStateProvider provider = Create(inspector, out FakeServiceController controller);

        await provider.WriteAsync(
            Key("SomeProvider"), StateValue.FromString("Stopped"), CancellationToken.None);

        Assert.Empty(controller.Stopped);
    }

    [Fact]
    public async Task Write_AlwaysAllowsStartingAServiceBackUp()
    {
        // Restoring is putting the machine back the way it was, so it is never refused.
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("RpcSs", state: ServiceState.Stopped));

        ServiceStateProvider provider = Create(inspector, out FakeServiceController controller);

        await provider.WriteAsync(Key("RpcSs"), StateValue.FromString("Running"), CancellationToken.None);

        Assert.Equal("RpcSs", Assert.Single(controller.Started));
    }

    [Fact]
    public async Task Write_OfAnAbsentValueDoesNothing()
    {
        var inspector = new FakeServiceInspector(ServiceFixtures.Service("SomeUpdater"));
        ServiceStateProvider provider = Create(inspector, out FakeServiceController controller);

        await provider.WriteAsync(Key("SomeUpdater"), StateValue.Absent, CancellationToken.None);

        Assert.Empty(controller.Stopped);
        Assert.Empty(controller.Started);
    }

    [Fact]
    public void ServiceControlAlwaysNeedsElevation()
    {
        ServiceStateProvider provider = Create(new FakeServiceInspector(), out _);

        Assert.True(provider.RequiresElevation(Key("Anything")));
    }

    private static ServiceStateProvider Create(
        FakeServiceInspector inspector,
        out FakeServiceController controller)
    {
        controller = new FakeServiceController(inspector);
        return new ServiceStateProvider(inspector, controller, NullLogger<ServiceStateProvider>.Instance);
    }

    private static StateKey Key(string name) =>
        new(ServiceStateProvider.Scheme, name, ServiceStateProvider.StateItem);
}
