using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Velocity.Abstractions.Hosting;
using Velocity.Diagnostics.Logging;
using Velocity.Abstractions.Diagnostics;

namespace Velocity.Diagnostics;

/// <summary>Registers the logging and diagnostics stack.</summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Adds filesystem layout, redaction, the Serilog backed logger factory, runtime verbosity
    /// control and support bundle export.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <param name="paths">Product filesystem layout.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddVelocityDiagnostics(
        this IServiceCollection services,
        IVelocityPaths paths,
        Action<LoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        var options = new LoggingOptions();
        configure?.Invoke(options);
        paths.EnsureCreated();

        var redactor = new SensitiveDataRedactor();
        var levelSwitch = new LoggingLevelSwitch(SerilogVerbosityController.ToSerilogLevel(options.MinimumLevel));

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                formatter: RedactingTextFormatter.CreateDefault(redactor),
                path: Path.Combine(paths.LogDirectory, $"{options.FileNamePrefix}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCount,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true);

        if (options.WriteToConsole)
        {
            configuration = configuration.WriteTo.Console(
                formatter: RedactingTextFormatter.CreateDefault(redactor));
        }

        Logger logger = configuration.CreateLogger();

        services.TryAddSingleton(paths);
        services.TryAddSingleton<ISensitiveDataRedactor>(redactor);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ILogVerbosityController>(
            _ => new SerilogVerbosityController(levelSwitch, options));
        services.TryAddSingleton<ISupportBundleExporter, SupportBundleExporter>();
        services.TryAddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(logger, dispose: true));
        services.TryAddSingleton(typeof(ILogger<>), typeof(Microsoft.Extensions.Logging.Logger<>));

        return services;
    }
}
