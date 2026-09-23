using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Presentation.ViewModels;
using Velocity.TestSupport;

namespace Velocity.Presentation.Tests;

/// <summary>
/// The dashboard is the whole product for most users, so its honesty properties are tested: an
/// unavailable counter must read as a dash, not a zero, and the one button must report what it
/// actually did.
/// </summary>
public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task Initialize_DescribesTheMachineAndStartsTelemetry()
    {
        var monitor = new FakeSystemMonitor();
        DashboardViewModel viewModel = Create(monitor);

        await viewModel.InitializeAsync();

        Assert.Contains("Ryzen", viewModel.MachineSummary, StringComparison.Ordinal);
        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(MonitorCadence.Foreground, monitor.Cadence);
    }

    [Fact]
    public void ASampleWithEveryCounter_FillsTheCards()
    {
        DashboardViewModel viewModel = Create(new FakeSystemMonitor());

        viewModel.ApplySample(new TelemetrySample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuUtilization = 0.37d,
            CpuFrequencyMhz = 4820d,
            GpuUtilization = 0.91d,
            MemoryUsedBytes = 16L * 1024 * 1024 * 1024,
            MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
            DiskBytesPerSecond = 52_428_800d,
        });

        Assert.Equal("37%", viewModel.CpuCard.Value);
        Assert.Equal("4820 MHz", viewModel.CpuCard.Detail);
        Assert.Equal("91%", viewModel.GpuCard.Value);
        Assert.Equal("50%", viewModel.MemoryCard.Value);
        Assert.Equal("50 MB/s", viewModel.DiskCard.Value);
    }

    [Fact]
    public void AnUnavailableCounter_ShowsADashRatherThanZero()
    {
        DashboardViewModel viewModel = Create(new FakeSystemMonitor());

        viewModel.ApplySample(new TelemetrySample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuUtilization = 0.5d,
        });

        Assert.True(viewModel.GpuCard.IsUnavailable);
        Assert.Equal("—", viewModel.GpuCard.Value);
        Assert.False(viewModel.CpuCard.IsUnavailable);
    }

    [Fact]
    public void PerCoreUtilization_IsUpdatedInPlaceRatherThanRebuilt()
    {
        DashboardViewModel viewModel = Create(new FakeSystemMonitor());

        viewModel.ApplySample(Sample(new[] { 0.1d, 0.2d, 0.3d }));
        viewModel.ApplySample(Sample(new[] { 0.9d, 0.8d, 0.7d }));

        // Rebuilding the collection on every tick would make the core strip flicker.
        Assert.Equal(new[] { 0.9d, 0.8d, 0.7d }, viewModel.PerCoreUtilization);
    }

    [Fact]
    public void ASampleFromTheMonitor_ReachesTheCards()
    {
        var monitor = new FakeSystemMonitor();
        DashboardViewModel viewModel = Create(monitor);

        monitor.Publish(new TelemetrySample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuUtilization = 0.25d,
        });

        Assert.Equal("25%", viewModel.CpuCard.Value);
    }

    [Fact]
    public void Dispose_StopsListeningToTheMonitor()
    {
        var monitor = new FakeSystemMonitor();
        DashboardViewModel viewModel = Create(monitor);

        Assert.Equal(1, monitor.SubscriberCount);

        viewModel.Dispose();

        // The monitor outlives the page; a leaked subscription would keep a dead view model alive.
        Assert.Equal(0, monitor.SubscriberCount);
    }

    [Fact]
    public async Task OptimizeNow_SaysSoPlainlyWhenThereAreNoModules()
    {
        var engine = new FakeOptimizationEngine();
        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), engine);

        await viewModel.OptimizeNowAsync();

        Assert.Empty(engine.Requests);
        Assert.Contains(
            "no optimization modules",
            Assert.Single(viewModel.LastRunSummary),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizeNow_AppliesEveryModuleUnderTheSelectedProfile()
    {
        var engine = new FakeOptimizationEngine();
        var tweak = new ScriptedTweak(
            "cpu.test",
            new Abstractions.State.StateKey("memory", "p", "v"),
            Abstractions.State.StateValue.FromUInt32(1));

        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), engine, new[] { tweak });
        viewModel.SelectedProfile = ProfileKind.Competitive;

        await viewModel.OptimizeNowAsync();

        OptimizationRequest request = Assert.Single(engine.Requests);
        Assert.Equal("cpu.test", Assert.Single(request.TweakIds));
        Assert.Equal(ProfileKind.Competitive.ToString(), request.ProfileId);

        // One unavailable module must not abandon the rest of the profile.
        Assert.False(request.AtomicAllOrNothing);
    }

    [Fact]
    public async Task OptimizeNow_ReportsThatNothingNeededChanging()
    {
        var tweak = new ScriptedTweak(
            "cpu.test",
            new Abstractions.State.StateKey("memory", "p", "v"),
            Abstractions.State.StateValue.FromUInt32(1));

        var engine = new FakeOptimizationEngine
        {
            Result = new OptimizationRunResult
            {
                TransactionId = Guid.NewGuid(),
                Status = TransactionStatus.Applied,
                Results = new[]
                {
                    new TweakRunResult
                    {
                        TweakId = "cpu.test",
                        Compatibility = CompatibilityResult.Supported(),
                        Outcome = ApplyOutcome.NoChangeRequired,
                        Message = "Already configured.",
                    },
                },
            },
        };

        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), engine, new[] { tweak });

        await viewModel.OptimizeNowAsync();

        Assert.False(viewModel.IsOptimized);
        Assert.Equal("Nothing needed changing", viewModel.OptimizationState);
    }

    [Fact]
    public async Task OptimizeNow_ReportsAFailedRunHonestly()
    {
        var tweak = new ScriptedTweak(
            "cpu.test",
            new Abstractions.State.StateKey("memory", "p", "v"),
            Abstractions.State.StateValue.FromUInt32(1));

        var engine = new FakeOptimizationEngine
        {
            Result = new OptimizationRunResult
            {
                TransactionId = Guid.NewGuid(),
                Status = TransactionStatus.FailedAndRolledBack,
                Results = new[]
                {
                    new TweakRunResult
                    {
                        TweakId = "cpu.test",
                        Compatibility = CompatibilityResult.Supported(),
                        Outcome = ApplyOutcome.Failed,
                        Message = "The helper refused the write.",
                    },
                },
            },
        };

        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), engine, new[] { tweak });

        await viewModel.OptimizeNowAsync();

        Assert.Equal("Failed; the machine was put back", viewModel.OptimizationState);
        Assert.Contains("The helper refused the write.", viewModel.LastRunSummary[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_SaysSoWhenThereIsNothingToUndo()
    {
        var rollback = new FakeRollbackEngine { Result = null };
        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), rollback: rollback);

        await viewModel.RestoreAsync();

        Assert.Contains("nothing applied", viewModel.LastRunSummary[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_ReportsWhatWasPutBack()
    {
        var rollback = new FakeRollbackEngine
        {
            Result = new RollbackResult(
                Guid.NewGuid(), 3, new Dictionary<string, string>(StringComparer.Ordinal)),
        };

        DashboardViewModel viewModel = Create(new FakeSystemMonitor(), rollback: rollback);

        await viewModel.RestoreAsync();

        Assert.Contains("Restored 3 value(s).", viewModel.LastRunSummary);
        Assert.False(viewModel.IsOptimized);
    }

    private static TelemetrySample Sample(IReadOnlyList<double> perCore) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        PerCoreUtilization = perCore,
    };

    private static DashboardViewModel Create(
        FakeSystemMonitor monitor,
        FakeOptimizationEngine? engine = null,
        IEnumerable<ITweak>? tweaks = null,
        FakeRollbackEngine? rollback = null) =>
        new(monitor,
            new FakeSystemProfileProvider(
                MachineFixtures.ProfileFor(MachineFixtures.AmdDualChipletAsymmetricCache())),
            engine ?? new FakeOptimizationEngine(),
            new TweakRegistry(tweaks ?? Array.Empty<ITweak>()),
            rollback ?? new FakeRollbackEngine(),
            NullLogger<DashboardViewModel>.Instance);
}
