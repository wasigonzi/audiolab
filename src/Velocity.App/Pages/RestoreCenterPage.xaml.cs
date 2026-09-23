using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.App.Pages;

/// <summary>Everything this product has changed, and how to put it back.</summary>
public sealed partial class RestoreCenterPage : Page
{
    /// <summary>Creates the page.</summary>
    public RestoreCenterPage()
    {
        InitializeComponent();
        Name = "RestoreRoot";
    }

    /// <summary>The view model behind the page.</summary>
    public RestoreCenterViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.Services.GetRequiredService<RestoreCenterViewModel>();
        DataContext = ViewModel;

        await ViewModel.LoadAsync();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel?.Dispose();
        ViewModel = null;
        base.OnNavigatedFrom(e);
    }
}
