using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hosting;
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

        services.TryAddSingleton<IDatabaseConnectionFactory>(provider =>
            new SqliteConnectionFactory(provider.GetRequiredService<IVelocityPaths>()));

        // Constructed explicitly rather than by type. DatabaseMigrator also has a constructor
        // taking an explicit migration set for tests, and the container prefers it: an
        // unregistered IEnumerable<T> resolves to an empty sequence instead of failing, so
        // registering by type produced a migrator with no migrations, a database with nothing in
        // it but schema_version, and a "no such table" on the first query. Naming the constructor
        // here is what makes that impossible.
        services.TryAddSingleton<IDatabaseMigrator>(provider => new DatabaseMigrator(
            provider.GetRequiredService<IDatabaseConnectionFactory>(),
            provider.GetRequiredService<ILogger<DatabaseMigrator>>()));
        services.TryAddSingleton<ITransactionJournal, TransactionJournal>();
        services.TryAddSingleton<IAuditRepository, AuditRepository>();
        services.TryAddSingleton<IAppliedTweakRepository, AppliedTweakRepository>();
        services.TryAddSingleton<ISystemProfileRepository, SystemProfileRepository>();
        services.TryAddSingleton<IProfileRepository, ProfileRepository>();
        services.TryAddSingleton<IBenchmarkRepository, BenchmarkRepository>();
        services.TryAddSingleton<ISettingsRepository, SettingsRepository>();
        services.TryAddSingleton<ITrialResultRepository, TrialResultRepository>();

        return services;
    }
}
