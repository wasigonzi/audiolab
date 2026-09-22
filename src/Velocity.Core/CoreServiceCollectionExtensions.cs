using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.State;
using Velocity.Core.Auditing;
using Velocity.Core.Games;
using Velocity.Core.Hardware;
using Velocity.Core.Network;
using Velocity.Core.Profiles;
using Velocity.Core.Sessions;
using Velocity.Core.State;
using Velocity.Core.Telemetry;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;

namespace Velocity.Core;

/// <summary>Registers the platform independent optimization engine.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the tweak catalogue, transaction coordinator, rollback engine, crash recovery and the
    /// system profile provider.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Hardware probes, state providers and the privilege context are not registered here: they
    /// are platform specific and come from the platform package. That separation is what allows
    /// the entire engine to be exercised on a build agent with no Windows present.
    /// </remarks>
    public static IServiceCollection AddVelocityCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStateProviderRegistry>(provider =>
            new StateProviderRegistry(provider.GetServices<IStateProvider>()));
        services.TryAddSingleton<ISystemProfileProvider, SystemProfileProvider>();
        services.TryAddSingleton<ITweakRegistry>(provider =>
            new TweakRegistry(provider.GetServices<Abstractions.Tweaks.ITweak>()));
        services.TryAddSingleton<ITweakContextFactory, TweakContextFactory>();
        services.TryAddSingleton<IAuditSink, AuditSink>();
        services.TryAddSingleton<IRollbackEngine, RollbackEngine>();
        services.TryAddSingleton<IOptimizationEngine, OptimizationEngine>();
        services.TryAddSingleton<ICrashRecoveryService, CrashRecoveryService>();
        services.TryAddSingleton<IAppliedTweakReader, AppliedTweakReader>();
        services.TryAddSingleton<Abstractions.Network.INetworkQualityProbe, PingNetworkQualityProbe>();
        services.TryAddSingleton<SystemMonitorOptions>();
        services.TryAddSingleton<Abstractions.Telemetry.ISystemMonitor>(provider => new SystemMonitor(
            provider.GetServices<Abstractions.Telemetry.ITelemetryProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SystemMonitor>>(),
            provider.GetRequiredService<SystemMonitorOptions>(),
            provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IProfileService, ProfileService>();
        services.TryAddSingleton<Abstractions.Games.IGameLibrary>(provider => new GameLibrary(
            provider.GetServices<Abstractions.Games.IGameLibrarySource>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GameLibrary>>()));
        services.TryAddSingleton<IUserDeclaredGames, UserDeclaredGames>();
        services.TryAddSingleton<Abstractions.Games.IGameDetector, GameDetector>();
        services.TryAddSingleton<GamingSessionOptions>();
        services.TryAddSingleton<IGamingSessionManager, GamingSessionManager>();

        return services;
    }
}
