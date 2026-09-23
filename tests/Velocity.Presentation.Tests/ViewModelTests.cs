using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.Tweaks;
using Velocity.Presentation;
using Velocity.Presentation.Mvvm;
using Velocity.Presentation.Navigation;
using Velocity.Presentation.ViewModels;
using Velocity.TestSupport;

namespace Velocity.Presentation.Tests;

/// <summary>
/// The view models carry no UI framework reference, which is what makes these tests possible at
/// all: the page logic is verified without a window, a dispatcher or a rendering pass.
/// </summary>
public sealed class SystemInformationViewModelTests
{
    [Fact]
    public async Task Load_PopulatesTheFactGroups()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await viewModel.LoadAsync(forceRefresh: false);

        Assert.Contains(viewModel.Groups, group => group.Title == "Processor");
        Assert.Contains(viewModel.Groups, group => group.Title == "Memory");
        Assert.Contains(viewModel.Groups, group => group.Title == "Graphics");
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Load_ShowsTheCoreComplexBreakdownOnAMultiChipletPart()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await viewModel.LoadAsync(forceRefresh: false);

        SystemFactGroup processor = viewModel.Groups.Single(group => group.Title == "Processor");
        Assert.Contains(processor.Facts, fact => fact.Label == "Core complexes");
    }

    [Fact]
    public async Task Load_ShowsTheHybridBreakdownOnAHybridPart()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.IntelHybridPerformanceAndEfficiency());

        await viewModel.LoadAsync(forceRefresh: false);

        SystemFactGroup processor = viewModel.Groups.Single(group => group.Title == "Processor");
        Assert.Contains(processor.Facts, fact => fact.Label == "Hybrid layout");
    }

    [Fact]
    public async Task Load_SurfacesTheTopologyNotes()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.AmdDualChipletAsymmetricCache());

        await viewModel.LoadAsync(forceRefresh: false);

        Assert.NotEmpty(viewModel.CpuNotes);
    }

    [Fact]
    public async Task Load_ShowsProbeFailuresRatherThanHidingThem()
    {
        SystemProfile profile = MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore()) with
        {
            ProbeFailures = new Dictionary<string, string> { ["storage"] = "WMI query failed." },
        };

        var provider = new FakeSystemProfileProvider(profile);
        var viewModel = new SystemInformationViewModel(
            provider, NullLogger<SystemInformationViewModel>.Instance);

        await viewModel.LoadAsync(forceRefresh: false);

        Assert.Contains("storage", Assert.Single(viewModel.ProbeFailures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceRefresh_ReprobesTheMachine()
    {
        var provider = new FakeSystemProfileProvider(
            MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore()));
        var viewModel = new SystemInformationViewModel(
            provider, NullLogger<SystemInformationViewModel>.Instance);

        await viewModel.LoadAsync(forceRefresh: false);
        Assert.Equal(0, provider.RefreshCount);

        await viewModel.LoadAsync(forceRefresh: true);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task Load_ReportsTheFingerprintSoSupportCanCorrelate()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.IntelDesktopEightCore());

        await viewModel.LoadAsync(forceRefresh: false);

        Assert.Equal(12, viewModel.FingerprintId.Length);
    }

    [Fact]
    public async Task Load_ClearsTheBusyFlagWhenItFinishes()
    {
        SystemInformationViewModel viewModel = Create(MachineFixtures.IntelDesktopEightCore());

        await viewModel.LoadAsync(forceRefresh: false);

        Assert.False(viewModel.IsBusy);
        Assert.Null(viewModel.BusyMessage);
    }

    [Fact]
    public async Task Load_CapturesAFailureInsteadOfThrowing()
    {
        var viewModel = new SystemInformationViewModel(
            new ThrowingProfileProvider(), NullLogger<SystemInformationViewModel>.Instance);

        await viewModel.LoadAsync(forceRefresh: true);

        Assert.Equal("The probe failed.", viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    private static SystemInformationViewModel Create(CpuTopology topology) =>
        new(new FakeSystemProfileProvider(MachineFixtures.ProfileFor(topology)),
            NullLogger<SystemInformationViewModel>.Instance);

    private sealed class ThrowingProfileProvider : ISystemProfileProvider
    {
        public Task<SystemProfile> GetAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The probe failed.");

        public Task<SystemProfile> RefreshAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The probe failed.");
    }
}

/// <summary>Tests for the shell: sidebar composition, mode switching and the privilege banner.</summary>
public sealed class ShellViewModelTests
{
    [Fact]
    public void Sidebar_ContainsAnEntryForEveryTweakCategory()
    {
        IReadOnlyList<NavigationItem> items = NavigationCatalogue.Build();

        foreach (TweakCategory category in Enum.GetValues<TweakCategory>())
        {
            Assert.Contains(items, item => item.Category == category);
        }
    }

    [Fact]
    public void Sidebar_StartsOnTheDashboard()
    {
        ShellViewModel shell = CreateShell();

        Assert.Equal(NavigationCatalogue.DashboardRoute, shell.SelectedItem.Key);
    }

    [Fact]
    public void Navigate_MovesToARegisteredRoute()
    {
        ServiceProvider services = BuildServices();
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.RegisterVelocityRoutes();

        var shell = services.GetRequiredService<ShellViewModel>();
        NavigationItem systemPage = shell.NavigationItems.Single(
            item => item.Key == NavigationCatalogue.SystemInformationRoute);

        shell.Navigate(systemPage);

        Assert.Equal(NavigationCatalogue.SystemInformationRoute, navigation.CurrentRoute);
        Assert.IsType<SystemInformationViewModel>(navigation.CurrentViewModel);
    }

    [Fact]
    public void Navigate_ToARouteWithNoPageYet_ReportsItInsteadOfThrowing()
    {
        ShellViewModel shell = CreateShell();
        NavigationItem cpuPage = shell.NavigationItems.First(item => item.Category == TweakCategory.Cpu);

        shell.Navigate(cpuPage);

        Assert.NotNull(shell.ErrorMessage);
    }

    [Fact]
    public void SetMode_UpdatesTheSharedModeService()
    {
        ServiceProvider services = BuildServices();
        var modeService = services.GetRequiredService<IApplicationModeService>();
        var shell = services.GetRequiredService<ShellViewModel>();

        shell.SetMode(ApplicationMode.Expert);

        Assert.Equal(ApplicationMode.Expert, modeService.Mode);
        Assert.Equal(ApplicationMode.Expert, shell.Mode);
    }

    [Fact]
    public void PrivilegeBanner_ExplainsAMissingHelper()
    {
        ShellViewModel shell = CreateShell(PrivilegeChannel.None);

        Assert.False(shell.PrivilegedOperationsAvailable);
        Assert.Contains("helper service is not running", shell.PrivilegeWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegeBanner_IsSilentWhenTheHelperIsRunning()
    {
        ShellViewModel shell = CreateShell();

        Assert.True(shell.PrivilegedOperationsAvailable);
        Assert.Null(shell.PrivilegeWarning);
    }

    [Fact]
    public void PrivilegeBanner_NotesWhenTheInterfaceIsRunningElevated()
    {
        ShellViewModel shell = CreateShell(PrivilegeChannel.DirectElevation);

        Assert.Contains("running elevated", shell.PrivilegeWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_DisposesThePageItLeaves()
    {
        ServiceProvider services = BuildServices();
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.Register<SystemInformationViewModel>("one");
        navigation.Register<SystemInformationViewModel>("two");

        navigation.NavigateTo("one");
        object first = navigation.CurrentViewModel!;
        navigation.NavigateTo("two");

        // A page that keeps running after the user leaves it is exactly the background overhead
        // this product exists to remove.
        Assert.NotSame(first, navigation.CurrentViewModel);
    }

    private static ShellViewModel CreateShell(PrivilegeChannel channel = PrivilegeChannel.HelperService) =>
        BuildServices(channel).GetRequiredService<ShellViewModel>();

    private static ServiceProvider BuildServices(PrivilegeChannel channel = PrivilegeChannel.HelperService)
    {
        var services = new ServiceCollection();
        services.AddVelocityPresentation();
        services.AddSingleton<IPrivilegeContext>(new FakePrivilegeContext(channel));
        services.AddSingleton<ISystemProfileProvider>(new FakeSystemProfileProvider(
            MachineFixtures.ProfileFor(MachineFixtures.IntelDesktopEightCore())));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        return services.BuildServiceProvider();
    }
}
