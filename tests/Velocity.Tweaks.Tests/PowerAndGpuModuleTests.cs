using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Power;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.Power;
using Velocity.Core.State;
using Velocity.Core.Transactions;
using Velocity.TestSupport;
using Velocity.Tweaks;
using Velocity.Tweaks.Gpu;
using Velocity.Tweaks.Power;

namespace Velocity.Tweaks.Tests;

/// <summary>
/// Covers the power plan module and the hardware scheduling module.
/// </summary>
/// <remarks>
/// Both are settings whose real effect depends on the machine, so the tests are mostly about what
/// the modules refuse to do: no plan change on battery, no claim that a restart-gated registry
/// value is already in effect, and an exact restore of whatever plan the user actually had.
/// </remarks>
public sealed class PowerAndGpuModuleTests
{
    private static readonly Guid Balanced = PowerSettings.BalancedScheme;
    private static readonly Guid HighPerformance = PowerSettings.HighPerformanceScheme;

    [Fact]
    public async Task ThePowerPlanModuleRefusesToRunOnBattery()
    {
        TweakContext context = PowerContext(out _, onBattery: true);

        CompatibilityResult result = await new PowerPlanTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.Blocked, result.Status);
        Assert.Contains("battery", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APluggedInLaptopIsNotBlocked()
    {
        TweakContext context = PowerContext(out _, hasBattery: true, onBattery: false);

        CompatibilityResult result = await new PowerPlanTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.True(result.IsSupported);
    }

    [Fact]
    public async Task APlanThatThisEditionDoesNotHaveIsReportedRatherThanAttempted()
    {
        // Ultimate performance is absent on most consumer editions.
        TweakContext context = PowerContext(
            out _,
            availableSchemes: [new PowerScheme(Balanced, "Balanced", IsActive: true)],
            options: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PowerPlanTweak.SchemeOption] =
                    PowerSettings.UltimatePerformanceScheme.ToString("D", CultureInfo.InvariantCulture),
            });

        CompatibilityResult result = await new PowerPlanTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedOperatingSystem, result.Status);
    }

    [Fact]
    public async Task ThePlanAlreadyInUseIsReportedAsOptimal()
    {
        TweakContext context = PowerContext(out _, activeScheme: HighPerformance);

        CompatibilityResult result = await new PowerPlanTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.AlreadyOptimal, result.Status);
    }

    [Fact]
    public async Task ApplySwitchesThePlanAndVerifyConfirmsItAgainstTheMachine()
    {
        TweakContext context = PowerContext(out FakePowerConfigurationController controller);
        var module = new PowerPlanTweak();

        ApplyResult applied = await module.ApplyAsync(context, CancellationToken.None);
        VerificationResult verified = await module.VerifyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, applied.Outcome);
        Assert.Equal(HighPerformance, controller.ActiveScheme);
        Assert.Equal(VerificationStatus.Verified, verified.Status);
    }

    [Fact]
    public async Task TheSnapshotCapturesTheUsersOwnPlanRatherThanAssumingBalanced()
    {
        var custom = new Guid("11111111-2222-3333-4444-555555555555");
        TweakContext context = PowerContext(out FakePowerConfigurationController controller,
            activeScheme: custom,
            availableSchemes:
            [
                new PowerScheme(custom, "My tuned plan", IsActive: true),
                new PowerScheme(HighPerformance, "High performance", IsActive: false),
            ]);

        var module = new PowerPlanTweak();
        StateKey key = Assert.Single(await module.GetStateKeysAsync(context, CancellationToken.None));

        StateValue captured = await context.State.ReadAsync(key, CancellationToken.None);
        await module.ApplyAsync(context, CancellationToken.None);
        await context.State.WriteAsync(key, captured, CancellationToken.None);

        Assert.Equal(custom, controller.ActiveScheme);
    }

    [Fact]
    public async Task VerifyReportsAMismatchWhenWindowsKeptTheOldPlan()
    {
        TweakContext context = PowerContext(out FakePowerConfigurationController controller);
        controller.KnownSchemes.Add(Balanced);

        var module = new PowerPlanTweak();
        await module.ApplyAsync(context, CancellationToken.None);

        VerificationResult verified = await module.VerifyAsync(context, CancellationToken.None);
        Assert.Equal(VerificationStatus.Mismatch, verified.Status);
    }

    [Fact]
    public void ThePowerPlanModuleDoesNotClaimFrames()
    {
        TweakDescriptor descriptor = new PowerPlanTweak().Descriptor;

        Assert.Contains("usually changes nothing measurable", descriptor.ExpectedEffect, StringComparison.Ordinal);
        Assert.Contains("DC side is untouched", descriptor.TechnicalDescription, StringComparison.Ordinal);
        Assert.True(descriptor.BenchmarkRecommended);
    }

    [Fact]
    public async Task HardwareSchedulingIsNeverReportedAsAppliedBeforeARestart()
    {
        TweakContext context = GpuContext(out InMemoryStateProvider provider);
        provider.Seed(RegistryKeys.Value(RegistryKeys.GraphicsDrivers, "HwSchMode"), StateValue.FromUInt32(1));

        var module = new HardwareSchedulingTweak();
        ApplyResult applied = await module.ApplyAsync(context, CancellationToken.None);
        VerificationResult verified = await module.VerifyAsync(context, CancellationToken.None);

        Assert.Equal(ApplyOutcome.AppliedPendingRestart, applied.Outcome);
        Assert.Equal(VerificationStatus.PendingRestart, verified.Status);
        Assert.Contains("does not claim it is in effect", verified.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentHwSchModeIsReportedAsUnknownRatherThanOff()
    {
        TweakContext context = GpuContext(out _);

        TweakObservation observation = await new HardwareSchedulingTweak()
            .DetectAsync(context, CancellationToken.None);

        Assert.Equal(AppliedState.NotApplied, observation.State);
        Assert.Contains("never offered it", observation.CurrentValueSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatReportsNoSupportMakesTheModuleUnsupported()
    {
        TweakContext context = GpuContext(out _, scheduling: HardwareSchedulingState.NotSupported);

        CompatibilityResult result = await new HardwareSchedulingTweak()
            .CheckCompatibilityAsync(context, CancellationToken.None);

        Assert.Equal(CompatibilityStatus.UnsupportedHardware, result.Status);
    }

    [Fact]
    public async Task TheModuleCanBeDrivenBackwardsForAnAutoTuneTrial()
    {
        TweakContext context = GpuContext(
            out InMemoryStateProvider provider,
            options: new Dictionary<string, string>(StringComparer.Ordinal) { ["enabled"] = "false" });

        provider.Seed(RegistryKeys.Value(RegistryKeys.GraphicsDrivers, "HwSchMode"), StateValue.FromUInt32(2));

        await new HardwareSchedulingTweak().ApplyAsync(context, CancellationToken.None);

        Assert.Equal(
            1u,
            (await provider.ReadAsync(
                RegistryKeys.Value(RegistryKeys.GraphicsDrivers, "HwSchMode"),
                CancellationToken.None)).AsUInt32());
    }

    [Fact]
    public void TheHardwareSchedulingModuleSaysTheEvidenceIsMixed()
    {
        TweakDescriptor descriptor = new HardwareSchedulingTweak().Descriptor;

        Assert.Contains("genuinely mixed", descriptor.ExpectedEffect, StringComparison.Ordinal);
        Assert.True(descriptor.RequiresRestart);
        Assert.True(descriptor.BenchmarkRecommended);
    }

    [Fact]
    public void EveryModuleInThisPhaseDeclaresTheKeysItWrites()
    {
        // The engine refuses an undeclared write; declaring nothing would make a module unusable.
        Assert.Equal(TweakCategory.Power, new PowerPlanTweak().Descriptor.Category);
        Assert.Equal(TweakCategory.Gpu, new HardwareSchedulingTweak().Descriptor.Category);
    }

    [Fact]
    public async Task FullPipeline_SwitchesThePlanAndTheRollbackPutsTheOldOneBack()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        await using EngineHarness harness = await EngineHarness.CreateAsync(
            [new PowerPlanTweak()],
            extraProviders: [new PowerStateProvider(controller, NullLogger<PowerStateProvider>.Instance)]);

        OptimizationRunResult applied = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = [PowerPlanTweak.TweakId],
                Reason = TransactionReason.GamingSession,
            },
            CancellationToken.None);

        Assert.Equal(TransactionStatus.Applied, applied.Status);
        Assert.Equal(HighPerformance, controller.ActiveScheme);

        await harness.Rollback.RollbackTransactionAsync(applied.TransactionId, CancellationToken.None);

        Assert.Equal(Balanced, controller.ActiveScheme);
    }

    [Fact]
    public async Task FullPipeline_LeavesThePlanAloneWithoutTheHelper()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        await using EngineHarness harness = await EngineHarness.CreateAsync(
            [new PowerPlanTweak()],
            privilegeChannel: PrivilegeChannel.None,
            extraProviders: [new PowerStateProvider(controller, NullLogger<PowerStateProvider>.Instance)]);

        OptimizationRunResult result = await harness.Engine.ApplyAsync(
            new OptimizationRequest
            {
                TweakIds = [PowerPlanTweak.TweakId],
                Reason = TransactionReason.ProfileApply,
            },
            CancellationToken.None);

        Assert.Equal(ApplyOutcome.Skipped, Assert.Single(result.Results).Outcome);
        Assert.Equal(Balanced, controller.ActiveScheme);
        Assert.Equal(0, controller.ActivationCount);
    }

    private static TweakContext PowerContext(
        out FakePowerConfigurationController controller,
        Guid? activeScheme = null,
        bool hasBattery = false,
        bool onBattery = false,
        IReadOnlyList<PowerScheme>? availableSchemes = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        Guid active = activeScheme ?? Balanced;
        controller = new FakePowerConfigurationController(active);

        var provider = new PowerStateProvider(controller, NullLogger<PowerStateProvider>.Instance);

        SystemProfile profile = MachineFixtures.ProfileFor(
            MachineFixtures.IntelDesktopEightCore(),
            hasBattery || onBattery ? MachineKind.Laptop : MachineKind.Desktop) with
        {
            Power = new PowerConfiguration
            {
                ActiveScheme = new PowerScheme(active, "Active", IsActive: true),
                AvailableSchemes = availableSchemes ?? [],
                ProcessorPolicy = new ProcessorPowerPolicy(),
                HasBattery = hasBattery || onBattery,
                IsOnBattery = onBattery,
            },
        };

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry([provider]),
            new FakePrivilegeContext(),
            PowerPlanTweak.TweakId,
            [PowerStateProvider.ActiveSchemeKey()]);

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance,
            options);
    }

    private static TweakContext GpuContext(
        out InMemoryStateProvider provider,
        HardwareSchedulingState scheduling = HardwareSchedulingState.Unknown,
        IReadOnlyDictionary<string, string>? options = null)
    {
        provider = new InMemoryStateProvider(RegistryKeys.Scheme);

        var adapter = new GpuDevice
        {
            DeviceInstanceId = @"PCI\VEN_10DE&DEV_2684",
            Description = "NVIDIA GeForce RTX 4090",
            Vendor = GpuVendor.Nvidia,
            DriverVersion = "32.0.15.6094",
            DedicatedVideoMemoryBytes = 24L * 1024 * 1024 * 1024,
            HardwareScheduling = scheduling,
        };

        SystemProfile profile = MachineFixtures.ProfileFor(
            MachineFixtures.IntelDesktopEightCore(), gpus: [adapter]);

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry([provider]),
            new FakePrivilegeContext(),
            HardwareSchedulingTweak.TweakId,
            [RegistryKeys.Value(RegistryKeys.GraphicsDrivers, "HwSchMode")]);

        return new TweakContext(
            profile,
            CpuTopologyAnalyzer.Analyze(profile.Cpu),
            accessor,
            new FakePrivilegeContext(),
            NullLogger.Instance,
            options);
    }
}
