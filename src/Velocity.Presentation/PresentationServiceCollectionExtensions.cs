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

        return services;
    }

    /// <summary>Registers the routes the view models in this build serve.</summary>
    /// <param name="navigation">Navigation service to configure.</param>
    /// <returns>The same navigation service.</returns>
    public static INavigationService RegisterVelocityRoutes(this INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        navigation.Register<SystemInformationViewModel>(NavigationCatalogue.SystemInformationRoute);
        return navigation;
    }
}
