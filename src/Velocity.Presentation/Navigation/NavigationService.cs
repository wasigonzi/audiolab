using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Presentation.Navigation;

/// <summary>One entry in the sidebar.</summary>
/// <param name="Key">Stable route key.</param>
/// <param name="Title">Display title.</param>
/// <param name="Glyph">Icon glyph the view layer renders.</param>
/// <param name="Category">Tweak category the page shows, when it is a category page.</param>
public sealed record NavigationItem(string Key, string Title, string Glyph, TweakCategory? Category);

/// <summary>Moves between pages without the view models knowing about the view layer.</summary>
public interface INavigationService
{
    /// <summary>The route that is currently displayed.</summary>
    string? CurrentRoute { get; }

    /// <summary>The view model for the current route.</summary>
    object? CurrentViewModel { get; }

    /// <summary>Raised after a successful navigation.</summary>
    event EventHandler<string>? Navigated;

    /// <summary>Registers a route.</summary>
    /// <typeparam name="TViewModel">View model type to resolve for the route.</typeparam>
    /// <param name="route">Route key.</param>
    void Register<TViewModel>(string route)
        where TViewModel : class;

    /// <summary>Navigates to a route.</summary>
    /// <param name="route">Route key.</param>
    /// <exception cref="InvalidOperationException">The route is not registered.</exception>
    void NavigateTo(string route);
}

/// <summary>
/// Default <see cref="INavigationService"/>.
/// </summary>
/// <remarks>
/// View models are resolved from the container per navigation and the previous one is disposed,
/// so a page's background work stops when the user leaves it. Keeping every page alive would mean
/// the optimizer polls telemetry for pages nobody is looking at, which is exactly the overhead the
/// product is supposed to remove.
/// </remarks>
public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the service.</summary>
    /// <param name="services">Container used to resolve view models.</param>
    public NavigationService(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public string? CurrentRoute { get; private set; }

    /// <inheritdoc />
    public object? CurrentViewModel { get; private set; }

    /// <inheritdoc />
    public event EventHandler<string>? Navigated;

    /// <inheritdoc />
    public void Register<TViewModel>(string route)
        where TViewModel : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        _routes[route] = typeof(TViewModel);
    }

    /// <inheritdoc />
    public void NavigateTo(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        if (!_routes.TryGetValue(route, out Type? viewModelType))
        {
            throw new InvalidOperationException($"Route '{route}' is not registered.");
        }

        object next = _services.GetRequiredService(viewModelType);

        if (CurrentViewModel is IDisposable previous && !ReferenceEquals(previous, next))
        {
            previous.Dispose();
        }

        CurrentViewModel = next;
        CurrentRoute = route;
        Navigated?.Invoke(this, route);
    }
}

/// <summary>The sidebar layout.</summary>
/// <remarks>
/// The categories mirror <see cref="TweakCategory"/> exactly. Keeping them in the same order as the
/// enum means a new category cannot be added to the engine and silently forgotten in the UI.
/// </remarks>
public static class NavigationCatalogue
{
    /// <summary>Route of the dashboard.</summary>
    public const string DashboardRoute = "dashboard";

    /// <summary>Route of the system information page.</summary>
    public const string SystemInformationRoute = "system-information";

    /// <summary>Route of the restore centre.</summary>
    public const string RestoreCentreRoute = "restore-centre";

    /// <summary>Route of the benchmark lab.</summary>
    public const string BenchmarkLabRoute = "benchmark-lab";

    /// <summary>Builds the sidebar entries.</summary>
    /// <returns>The navigation items in display order.</returns>
    public static IReadOnlyList<NavigationItem> Build()
    {
        var items = new List<NavigationItem>
        {
            new(DashboardRoute, "Dashboard", "", null),
            new(SystemInformationRoute, "System", "", null),
        };

        items.AddRange(Enum.GetValues<TweakCategory>()
            .Select(category => new NavigationItem(
                $"category/{category.ToString().ToLowerInvariant()}",
                DescribeCategory(category),
                GlyphFor(category),
                category)));

        items.Add(new NavigationItem(BenchmarkLabRoute, "Benchmark Lab", "", null));
        items.Add(new NavigationItem(RestoreCentreRoute, "Restore Centre", "", null));

        return new ReadOnlyCollection<NavigationItem>(items);
    }

    private static string DescribeCategory(TweakCategory category) => category switch
    {
        TweakCategory.Cpu => "CPU",
        TweakCategory.Gpu => "GPU",
        TweakCategory.Memory => "Memory",
        TweakCategory.Network => "Network",
        TweakCategory.Storage => "Storage",
        TweakCategory.Windows => "Windows",
        TweakCategory.Services => "Services",
        TweakCategory.Processes => "Processes",
        TweakCategory.Power => "Power",
        TweakCategory.Scheduler => "Scheduler",
        TweakCategory.Latency => "Latency",
        TweakCategory.Input => "Input",
        TweakCategory.Display => "Display",
        TweakCategory.Experimental => "Experimental",
        _ => category.ToString(),
    };

    private static string GlyphFor(TweakCategory category) => category switch
    {
        TweakCategory.Cpu => "",
        TweakCategory.Gpu => "",
        TweakCategory.Memory => "",
        TweakCategory.Network => "",
        TweakCategory.Storage => "",
        TweakCategory.Windows => "",
        TweakCategory.Services => "",
        TweakCategory.Processes => "",
        TweakCategory.Power => "",
        TweakCategory.Scheduler => "",
        TweakCategory.Latency => "",
        TweakCategory.Input => "",
        TweakCategory.Display => "",
        TweakCategory.Experimental => "",
        _ => "",
    };
}
