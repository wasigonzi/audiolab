using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Power;
using Velocity.Abstractions.State;
using Velocity.Core.Power;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers the state provider that makes power configuration snapshotable and reversible.
/// </summary>
/// <remarks>
/// The behaviour that matters here is the absent case. A machine that never exposed a setting must
/// not acquire one because the product ran, and the only way to guarantee that is for a hidden
/// setting to round-trip as <see cref="StateValueKind.Absent"/> and for restoring an absent value
/// to write nothing at all.
/// </remarks>
public sealed class PowerStateProviderTests
{
    private static readonly Guid Balanced = PowerSettings.BalancedScheme;
    private static readonly Guid HighPerformance = PowerSettings.HighPerformanceScheme;

    private static PowerStateProvider Create(FakePowerConfigurationController controller) =>
        new(controller, NullLogger<PowerStateProvider>.Instance);

    [Fact]
    public async Task TheActiveSchemeReadsBackAsItsGuid()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        StateValue value = await Create(controller)
            .ReadAsync(PowerStateProvider.ActiveSchemeKey(), CancellationToken.None);

        Assert.Equal(Balanced.ToString("D", CultureInfo.InvariantCulture), value.Data);
    }

    [Fact]
    public async Task AnUnreadableActiveSchemeIsAbsentRatherThanAnEmptyGuid()
    {
        var controller = new FakePowerConfigurationController(Guid.Empty);

        StateValue value = await Create(controller)
            .ReadAsync(PowerStateProvider.ActiveSchemeKey(), CancellationToken.None);

        Assert.True(value.IsAbsent);
    }

    [Fact]
    public async Task WritingTheActiveSchemeKeyActivatesThatScheme()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        await Create(controller).WriteAsync(
            PowerStateProvider.ActiveSchemeKey(),
            StateValue.FromString(HighPerformance.ToString("D", CultureInfo.InvariantCulture)),
            CancellationToken.None);

        Assert.Equal(HighPerformance, controller.ActiveScheme);
        Assert.Equal(1, controller.ActivationCount);
    }

    [Fact]
    public async Task AHiddenSettingReadsAsAbsentRatherThanZero()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        StateValue value = await Create(controller).ReadAsync(
            PowerStateProvider.SettingKey(
                Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MinimumProcessorState),
            CancellationToken.None);

        Assert.True(value.IsAbsent);
        Assert.NotEqual(StateValue.FromUInt32(0), value);
    }

    [Fact]
    public async Task RestoringAnAbsentSettingWritesNothing()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        await Create(controller).WriteAsync(
            PowerStateProvider.SettingKey(
                Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MinimumProcessorState),
            StateValue.Absent,
            CancellationToken.None);

        Assert.Empty(controller.Writes);
    }

    [Fact]
    public async Task AnExposedSettingRoundTrips()
    {
        var controller = new FakePowerConfigurationController(Balanced);
        controller.Expose(Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MinimumProcessorState, 5);

        PowerStateProvider provider = Create(controller);
        StateKey key = PowerStateProvider.SettingKey(
            Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MinimumProcessorState);

        StateValue captured = await provider.ReadAsync(key, CancellationToken.None);
        await provider.WriteAsync(key, StateValue.FromUInt32(100), CancellationToken.None);
        await provider.WriteAsync(key, captured, CancellationToken.None);

        StateValue restored = await provider.ReadAsync(key, CancellationToken.None);
        Assert.Equal(5u, restored.AsUInt32());
    }

    [Fact]
    public async Task ANonNumericSettingValueIsRefusedRatherThanGuessedAt()
    {
        var controller = new FakePowerConfigurationController(Balanced);

        await Create(controller).WriteAsync(
            PowerStateProvider.SettingKey(
                Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MaximumProcessorState),
            StateValue.FromString("mostly"),
            CancellationToken.None);

        Assert.Empty(controller.Writes);
    }

    [Fact]
    public async Task AMalformedSettingKeyIsRejected()
    {
        var controller = new FakePowerConfigurationController(Balanced);
        var key = new StateKey(PowerStateProvider.Scheme, "not/a/triple", PowerStateProvider.AcItem);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Create(controller).ReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public void EveryPowerWriteIsDeclaredAsNeedingElevation()
    {
        PowerStateProvider provider = Create(new FakePowerConfigurationController(Balanced));

        Assert.True(provider.RequiresElevation(PowerStateProvider.ActiveSchemeKey()));
        Assert.True(provider.RequiresElevation(PowerStateProvider.SettingKey(
            Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.PerformanceBoostMode)));
    }

    [Fact]
    public void SettingKeysCarryTheAcItemAndNeverADcOne()
    {
        // There is no DC key shape at all: the product does not decide battery behaviour.
        StateKey key = PowerStateProvider.SettingKey(
            Balanced, PowerSettings.ProcessorSubgroup, PowerSettings.MinimumProcessorState);

        Assert.Equal(PowerStateProvider.AcItem, key.Item);
        Assert.DoesNotContain("dc", key.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
