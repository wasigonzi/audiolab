using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Velocity.Presentation.Mvvm;
using Velocity.Presentation.Navigation;
using Velocity.Presentation.ViewModels;

namespace Velocity.Presentation;

/// <summary>Registers the MVVM infrastructure and the view models that exist in this phase.</summary>
public static class PresentationServiceCollectionExtensions
{
    /// <summary>Adds navigation, the mode service and the view models.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// View models are registered as transient so that navigating away disposes them and navigating
    /// back starts from a clean state, which is what keeps a page's telemetry timers from
    /// outliving the page.
    /// </remarks>
    public static IServiceCollection AddVelocityPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IApplicationModeService, ApplicationModeService>();
        services.TryAddSingleton<IUiDispatcher, InlineUiDispatcher>();
        services.TryAddSingleton<INavigationService, NavigationService>();

        services.TryAddTransient<ShellViewModel>();
        services.TryAddTransient<SystemInformationViewModel>();
        services.TryAddTransient<DashboardViewModel>();
        services.TryAddTransient<TweakCategoryViewModel>();
        services.TryAddTransient<RestoreCenterViewModel>();

        return services;
    }

    /// <summary>Registers the routes the view models in this build serve.</summary>
    /// <param name="navigation">Navigation service to configure.</param>
    /// <returns>The same navigation service.</returns>
    public static INavigationService RegisterVelocityRoutes(this INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        navigation.Register<DashboardViewModel>(NavigationCatalogue.DashboardRoute);
        navigation.Register<SystemInformationViewModel>(NavigationCatalogue.SystemInformationRoute);
        navigation.Register<RestoreCenterViewModel>(NavigationCatalogue.RestoreCentreRoute);

        // Every tweak category is served by the same page; the route carries which one.
        foreach (NavigationItem item in NavigationCatalogue.Build())
        {
            if (item.Category is not null)
            {
                navigation.Register<TweakCategoryViewModel>(item.Key);
            }
        }

        return navigation;
    }
}
