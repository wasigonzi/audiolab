using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Privileges;
using Velocity.Presentation.Mvvm;
using Velocity.Presentation.Navigation;

namespace Velocity.Presentation.ViewModels;

/// <summary>
/// The application shell: sidebar, mode selector and the privilege banner.
/// </summary>
/// <remarks>
/// The privilege banner is part of the shell rather than a per page detail because "the helper is
/// not running, so privileged tweaks are unavailable" is a statement about the whole application.
/// Showing it once, honestly, is better than letting each page fail differently.
/// </remarks>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly IApplicationModeService _modeService;
    private readonly IPrivilegeContext _privileges;

    /// <summary>Creates the shell.</summary>
    /// <param name="navigation">Navigation service.</param>
    /// <param name="modeService">Application mode service.</param>
    /// <param name="privileges">Privilege context.</param>
    /// <param name="logger">Logger.</param>
    public ShellViewModel(
        INavigationService navigation,
        IApplicationModeService modeService,
        IPrivilegeContext privileges,
        ILogger<ShellViewModel> logger)
        : base(logger)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _modeService = modeService ?? throw new ArgumentNullException(nameof(modeService));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));

        NavigationItems = new ReadOnlyCollection<NavigationItem>(NavigationCatalogue.Build().ToList());
        SelectedItem = NavigationItems[0];
        Mode = _modeService.Mode;
    }

    /// <summary>Sidebar entries.</summary>
    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    /// <summary>The sidebar entry currently selected.</summary>
    [ObservableProperty]
    public partial NavigationItem SelectedItem { get; set; }

    /// <summary>The active application mode.</summary>
    [ObservableProperty]
    public partial ApplicationMode Mode { get; set; }

    /// <summary>Whether privileged tweaks can be applied right now.</summary>
    public bool PrivilegedOperationsAvailable => _privileges.CanElevate;

    /// <summary>Banner text explaining the current privilege state, or null when all is well.</summary>
    public string? PrivilegeWarning => _privileges.Channel switch
    {
        PrivilegeChannel.None =>
            "The Velocity helper service is not running. Settings that need administrator rights are " +
            "shown but cannot be applied.",
        PrivilegeChannel.DirectElevation =>
            "This process is running elevated. The helper service is not required, but running the " +
            "interface unelevated is the supported configuration.",
        _ => null,
    };

    /// <summary>Navigates to the selected sidebar entry.</summary>
    /// <param name="item">Entry to navigate to.</param>
    [RelayCommand]
    public void Navigate(NavigationItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedItem = item;

        try
        {
            _navigation.NavigateTo(item.Key);
        }
        catch (InvalidOperationException ex)
        {
            // A route that exists in the sidebar but has no page yet is a build time gap, not a
            // user error; it is surfaced rather than silently swallowed.
            Logger.LogWarning(ex, "No page is registered for route {Route}.", item.Key);
            ErrorMessage = $"'{item.Title}' is not available in this build yet.";
        }
    }

    /// <summary>Switches the application mode.</summary>
    /// <param name="mode">Mode to switch to.</param>
    [RelayCommand]
    public void SetMode(ApplicationMode mode)
    {
        _modeService.SetMode(mode);
        Mode = _modeService.Mode;
    }
}
