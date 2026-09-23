using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Velocity.Abstractions.Tweaks;
using Velocity.App.Pages;
using Velocity.Presentation;
using Velocity.Presentation.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.App;

/// <summary>
/// The shell window: title bar, sidebar, privilege banner and the content frame.
/// </summary>
/// <remarks>
/// The window owns no logic. Navigation decisions come from <see cref="ShellViewModel"/>; this
/// class only maps a route to a page type and applies the system backdrop.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ShellViewModel _shell;

    /// <summary>Creates the window.</summary>
    /// <param name="services">Application services.</param>
    public MainWindow(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        InitializeComponent();

        // Mica is composited by the system, so the translucency costs the application nothing
        // per frame. On a machine that cannot render it, WinUI falls back to a solid colour.
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        _services.GetRequiredService<INavigationService>().RegisterVelocityRoutes();

        _shell = _services.GetRequiredService<ShellViewModel>();
        RootGrid.DataContext = _shell;

        Navigate(_shell.SelectedItem);
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationItem item)
        {
            _shell.Navigate(item);
            Navigate(item);
        }
    }

    private void Navigate(NavigationItem item)
    {
        Type pageType = item.Category is not null
            ? typeof(TweakCategoryPage)
            : item.Key switch
            {
                NavigationCatalogue.DashboardRoute => typeof(DashboardPage),
                NavigationCatalogue.SystemInformationRoute => typeof(SystemInformationPage),
                NavigationCatalogue.RestoreCentreRoute => typeof(RestoreCenterPage),
                _ => typeof(NotAvailablePage),
            };

        ContentFrame.Navigate(pageType, new PageContext(_services, item.Category));
    }
}

/// <summary>What a page needs to resolve its view model.</summary>
/// <param name="Services">Application services.</param>
/// <param name="Category">Tweak category, when the page serves one.</param>
public sealed record PageContext(IServiceProvider Services, TweakCategory? Category);
