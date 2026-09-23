using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.App.Pages;

/// <summary>Every module in one category, with its live state.</summary>
public sealed partial class TweakCategoryPage : Page
{
    /// <summary>Creates the page.</summary>
    public TweakCategoryPage()
    {
        InitializeComponent();
        Name = "PageRoot";
    }

    /// <summary>The view model behind the page.</summary>
    public TweakCategoryViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PageContext { Category: not null } context)
        {
            return;
        }

        ViewModel = context.Services.GetRequiredService<TweakCategoryViewModel>();
        DataContext = ViewModel;

        await ViewModel.LoadAsync(context.Category.Value);
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel?.Dispose();
        ViewModel = null;
        base.OnNavigatedFrom(e);
    }
}
