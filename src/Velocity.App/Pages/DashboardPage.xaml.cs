using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.App.Pages;

/// <summary>The dashboard.</summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>Creates the page.</summary>
    public DashboardPage() => InitializeComponent();

    /// <summary>The view model behind the page.</summary>
    public DashboardViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.Services.GetRequiredService<DashboardViewModel>();
        DataContext = ViewModel;

        await ViewModel.InitializeAsync();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Disposing unsubscribes the view model from the telemetry monitor, so a page the user has
        // left stops doing work.
        ViewModel?.Dispose();
        ViewModel = null;
        base.OnNavigatedFrom(e);
    }
}
