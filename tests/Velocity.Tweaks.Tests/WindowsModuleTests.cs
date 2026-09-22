using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Services;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.Services;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.TestSupport;
using Velocity.Tweaks;
using Velocity.Tweaks.Windows;

namespace Velocity.Tweaks.Tests;

/// <summary>Game Bar background recording.</summary>
public sealed class GameRecordingTweakTests
{
    private static readonly StateKey GameDvr =
        RegistryKeys.Value(RegistryKeys.GameConfigStore, "GameDVR_Enabled");

    private static readonly StateKey AppCapture = RegistryKeys.Value(
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");

    [Fact]
    public void ItNeedsNoAdministratorRights()
    {
        // Both values are per user, which is why this module works without the helper service.
        Assert.False(new GameRecordingTweak().Descriptor.RequiresElevation);
    }

    [Fact]
    public void ItDoesNotClaimToTouchGameMode()
    {
        TweakDescriptor descriptor = new GameRecordingTweak().Descriptor;

        Assert.Contains("Game Mode", descriptor.Summary, StringComparison.Ordinal);
        Assert.Contains("not touched", descriptor.TechnicalDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detect_ReportsRecordingAlreadyOffWhenTheValuesAreAbsent()
    {
        TweakContext context = Context(out _);

        TweakObservation observation = await new GameRecordingTweak()
            .DetectAsync(context, CancellationToken.None);

        // Absent means the user never enabled it, which is off, not unknown.
        Assert.Equal(AppliedState.Applied, observation.State);
    }

    [Fact]
    public async Task Detect_ReportsRecordingOnWhenEitherValueIsSet()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(AppCapture, StateValue.FromUInt32(1));

        TweakObservation observation = await new GameRecordingTweak()
            .DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.NotApplied, observation.State);
    }

    [Fact]
    public async Task Apply_TurnsOffOnlyWhatWasOn()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);
        provider.Seed(GameDvr, StateValue.FromUInt32(1));

        ApplyResult result = await new GameRecordingTweak().ApplyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, provider.WriteCount);
        Assert.Equal(StateValue.FromUInt32(0), await provider.ReadAsync(GameDvr, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_DoesNothingWhenRecordingIsAlreadyOff()
    {
        TweakContext context = Context(out InMemoryStateProvider provider);

        ApplyResult result = await new GameRecordingTweak().ApplyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.NoChangeRequired, result.Outcome);
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task FullPipeline_RollbackRestoresTheUsersOriginalChoice()
    {
        await using EngineHarness harness =
            await EngineHarness.CreateAsync(new ITweak[] { new GameRecordingTweak() });

        harness.RegistryState.Seed(GameDvr, StateValue.FromUInt32(1));

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { GameRecordingTweak.TweakId },
                Reason = TransactionReason.ManualTweak,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, applied.Status);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.Equal(
            StateValue.FromUInt32(1),
            await harness.RegistryState.ReadAsync(GameDvr, CancellationToken.None));

        // The value the user never had must not be invented on the way back.
        Assert.True((await harness.RegistryState.ReadAsync(AppCapture, CancellationToken.None)).IsAbsent);
    }

    private static TweakContext Context(out InMemoryStateProvider provider)
    {
        provider = new InMemoryStateProvider(RegistryKeys.Scheme);
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            GameRecordingTweak.TweakId,
            new[] { GameDvr, AppCapture });

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance);
    }
}

/// <summary>Pausing optional services for a session.</summary>
public sealed class SessionServiceTweakTests
{
    [Fact]
    public async Task ItOnlyTargetsServicesTheClassifierCallsOptional()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("RpcSs"),
            ServiceFixtures.Service("WinDefend"),
            ServiceFixtures.Service("BEService"),
            ServiceFixtures.Service("NVDisplay.ContainerLocalSystem"),
            ServiceFixtures.Service("SomeVendorUpdater"),
            ServiceFixtures.Service("AnotherUpdater"));

        var tweak = new SessionServiceTweak(inspector);
        TweakContext context = Context(inspector, out _);

        IReadOnlyList<StateKey> keys = await tweak.GetStateKeysAsync(context, CancellationToken.None);

        Assert.Equal(
            new[] { "AnotherUpdater", "SomeVendorUpdater" },
            keys.Select(key => key.Path).Order().ToArray());
    }

    [Fact]
    public async Task ItIsUnsupportedWhenNothingCanBePausedSafely()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("RpcSs"),
            ServiceFixtures.Service("WinDefend"));

        var tweak = new SessionServiceTweak(inspector);
        TweakContext context = Context(inspector, out _);

        CompatibilityResult result = await tweak.CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.AlreadyOptimal, result.Status);
    }

    [Fact]
    public async Task ItCapsHowManyServicesOneClickCanStop()
    {
        ServiceSnapshot[] many = Enumerable.Range(0, 40)
            .Select(index => ServiceFixtures.Service($"Updater{index:00}"))
            .ToArray();

        var inspector = new FakeServiceInspector(many);
        var tweak = new SessionServiceTweak(inspector);

        TweakContext context = Context(
            inspector,
            out _,
            new Dictionary<string, string> { [SessionServiceTweak.MaximumServicesOption] = "5" });

        Assert.Equal(5, (await tweak.GetStateKeysAsync(context, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Detect_BreaksTheMachineDownByClassification()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("RpcSs"),
            ServiceFixtures.Service("WinDefend"),
            ServiceFixtures.Service("SomeUpdater"),
            ServiceFixtures.Service("Idle", state: ServiceState.Stopped));

        var tweak = new SessionServiceTweak(inspector);
        TweakContext context = Context(inspector, out _);

        TweakObservation observation = await tweak.DetectAsync(context, CancellationToken.None);

        Assert.Equal("1", observation.Details[nameof(ServiceClassification.Critical)]);
        Assert.Equal("1", observation.Details[nameof(ServiceClassification.Security)]);
        Assert.Equal("1", observation.Details[nameof(ServiceClassification.Optional)]);
        Assert.Equal("SomeUpdater", observation.Details["candidates"]);
    }

    [Fact]
    public async Task FullPipeline_PausesAndThenRestartsEveryOptionalService()
    {
        var inspector = new FakeServiceInspector(
            ServiceFixtures.Service("SomeUpdater"),
            ServiceFixtures.Service("RpcSs"));

        var controller = new FakeServiceController(inspector);
        var provider = new ServiceStateProvider(
            inspector, controller, NullLogger<ServiceStateProvider>.Instance);

        await using EngineHarness harness = await EngineHarness.CreateAsync(
            new ITweak[] { new SessionServiceTweak(inspector) },
            extraProviders: new IStateProvider[] { provider });

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SessionServiceTweak.TweakId },
                Reason = TransactionReason.GamingSession,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, applied.Status);
        Assert.Equal("SomeUpdater", Assert.Single(controller.Stopped));
        Assert.DoesNotContain("RpcSs", controller.Stopped);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.Equal("SomeUpdater", Assert.Single(controller.Started));
        Assert.Equal(
            ServiceState.Running,
            inspector.Services.Single(service => service.Name == "SomeUpdater").State);
    }

    [Fact]
    public async Task AServiceThatRefusesToStop_IsReportedNotTreatedAsAFailure()
    {
        var inspector = new FakeServiceInspector(ServiceFixtures.Service("StubbornUpdater"));
        var controller = new FakeServiceController(inspector);
        controller.Stubborn.Add("StubbornUpdater");

        var provider = new ServiceStateProvider(
            inspector, controller, NullLogger<ServiceStateProvider>.Instance);

        await using EngineHarness harness = await EngineHarness.CreateAsync(
            new ITweak[] { new SessionServiceTweak(inspector) },
            extraProviders: new IStateProvider[] { provider });

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = new[] { SessionServiceTweak.TweakId },
                Reason = TransactionReason.GamingSession,
            },
            CancellationToken.None);

        // The machine is in a valid state either way and the journal still holds the original.
        Assert.Equal(TransactionStatus.Applied, result.Status);
        Assert.Contains("did not stop", result.Results[0].VerificationMessage!, StringComparison.Ordinal);
    }

    private static TweakContext Context(
        FakeServiceInspector inspector,
        out FakeServiceController controller,
        IReadOnlyDictionary<string, string>? options = null)
    {
        controller = new FakeServiceController(inspector);
        var provider = new ServiceStateProvider(
            inspector, controller, NullLogger<ServiceStateProvider>.Instance);

        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore());

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            SessionServiceTweak.TweakId,
            Array.Empty<StateKey>());

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance,
            options);
    }
}
