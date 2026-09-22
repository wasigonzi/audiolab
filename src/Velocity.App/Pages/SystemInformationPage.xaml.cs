using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.App.Pages;

/// <summary>Detected hardware, including what could not be detected.</summary>
public sealed partial class SystemInformationPage : Page
{
    /// <summary>Creates the page.</summary>
    public SystemInformationPage() => InitializeComponent();

    /// <summary>The view model behind the page.</summary>
    public SystemInformationViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.Services.GetRequiredService<SystemInformationViewModel>();
        DataContext = ViewModel;

        await ViewModel.LoadAsync(forceRefresh: false);
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel?.Dispose();
        ViewModel = null;
        base.OnNavigatedFrom(e);
    }
}
