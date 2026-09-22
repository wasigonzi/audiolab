using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hosting;
using Velocity.Core;
using Velocity.Core.Transactions;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Diagnostics;
using Velocity.Platform.Windows;
using Velocity.Presentation;

namespace Velocity.Composition;

/// <summary>
/// Builds the composition root shared by the desktop application and the command line host.
/// </summary>
/// <remarks>
/// The startup order matters and is enforced here rather than left to each host: the database is
/// migrated before anything reads it, and crash recovery runs before any new transaction can be
/// opened. Opening a second transaction while a previous one is still journalled as in-flight
/// would make the first one unrecoverable.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class VelocityHost
{
    /// <summary>Builds the service provider.</summary>
    /// <param name="paths">Filesystem layout to use, or <see langword="null"/> for the default.</param>
    /// <param name="writeToConsole">Whether log output should also go to the console.</param>
    /// <param name="configureServices">
    /// Host specific registrations, applied before the standard ones. The desktop application uses
    /// it to supply a real UI dispatcher; because every layer registers with TryAdd, whatever a
    /// host registers here wins.
    /// </param>
    /// <returns>The configured provider.</returns>
    public static ServiceProvider Build(
        IVelocityPaths? paths = null,
        bool writeToConsole = true,
        Action<IServiceCollection>? configureServices = null)
    {
        IVelocityPaths resolvedPaths = paths ?? new VelocityPaths();

        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        services.AddVelocityDiagnostics(resolvedPaths, options =>
        {
            options.WriteToConsole = writeToConsole;
            options.FileNamePrefix = "velocity-cli";
        });
        services.AddVelocityData();
        services.AddVelocityCore();
        services.AddVelocityWindowsPlatform();
        services.AddVelocityPresentation();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Migrates the database and rolls back any transaction that a previous run left in flight.
    /// </summary>
    /// <param name="services">The composition root.</param>
    /// <param name="cancellationToken">Token used to abort startup.</param>
    /// <returns>The recovery report.</returns>
    public static async Task<RecoveryReport> StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var migrator = services.GetRequiredService<IDatabaseMigrator>();
        int version = await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);

        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Velocity.Startup");
        logger.LogInformation("Database schema is at version {Version}.", version);

        var recovery = services.GetRequiredService<ICrashRecoveryService>();
        RecoveryReport report = await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);

        if (!report.NothingToDo)
        {
            logger.LogWarning(
                "Startup recovery restored {Recovered} transaction(s); {Failed} could not be restored.",
                report.RecoveredTransactionCount,
                report.FailedTransactionIds.Count);
        }

        return report;
    }
}
