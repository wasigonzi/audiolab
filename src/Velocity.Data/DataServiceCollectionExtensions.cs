using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;

namespace Velocity.Data;

/// <summary>Registers the persistence layer.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Adds the connection factory, migrator and repositories. The database file is created by
    /// the connection factory; migrations are applied by the startup sequence, not here, so that
    /// a failed migration surfaces as a startup error rather than a resolution error.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddVelocityData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDatabaseConnectionFactory, SqliteConnectionFactory>();
        services.TryAddSingleton<IDatabaseMigrator, DatabaseMigrator>();
        services.TryAddSingleton<ITransactionJournal, TransactionJournal>();
        services.TryAddSingleton<IAuditRepository, AuditRepository>();
        services.TryAddSingleton<IAppliedTweakRepository, AppliedTweakRepository>();
        services.TryAddSingleton<ISystemProfileRepository, SystemProfileRepository>();
        services.TryAddSingleton<IProfileRepository, ProfileRepository>();
        services.TryAddSingleton<IBenchmarkRepository, BenchmarkRepository>();
        services.TryAddSingleton<ISettingsRepository, SettingsRepository>();

        return services;
    }
}
