using System;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Abstractions.Tweaks;
using Velocity.Tweaks.Cpu;

namespace Velocity.Tweaks;

/// <summary>Registers the optimization modules that ship in this build.</summary>
public static class TweaksServiceCollectionExtensions
{
    /// <summary>
    /// Adds every module to the catalogue.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Modules are registered unconditionally and filtered per machine by the compatibility
    /// evaluator at runtime. Registering conditionally would hide a module from the catalogue
    /// entirely, and a user is better served by seeing a setting with "your hardware does not have
    /// this feature" next to it than by not seeing it at all.
    /// </remarks>
    public static IServiceCollection AddVelocityTweaks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITweak, SchedulerQuantumTweak>();

        return services;
    }
}
