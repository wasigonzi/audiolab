using System;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Velocity.Diagnostics.Logging;

/// <summary>Implements <see cref="ILogVerbosityController"/> over a Serilog level switch.</summary>
public sealed class SerilogVerbosityController : ILogVerbosityController
{
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LogEventLevel _configuredLevel;
    private readonly LogEventLevel _gamingLevel;

    /// <summary>Creates the controller.</summary>
    /// <param name="levelSwitch">Switch shared with the Serilog pipeline.</param>
    /// <param name="options">Configured levels.</param>
    public SerilogVerbosityController(LoggingLevelSwitch levelSwitch, LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _configuredLevel = ToSerilogLevel(options.MinimumLevel);
        _gamingLevel = ToSerilogLevel(options.GamingSessionMinimumLevel);
        _levelSwitch.MinimumLevel = _configuredLevel;
    }

    /// <inheritdoc />
    public LogLevel CurrentLevel => ToMicrosoftLevel(_levelSwitch.MinimumLevel);

    /// <inheritdoc />
    public void EnterGamingSession() =>
        _levelSwitch.MinimumLevel = _gamingLevel > _configuredLevel ? _gamingLevel : _configuredLevel;

    /// <inheritdoc />
    public void LeaveGamingSession() => _levelSwitch.MinimumLevel = _configuredLevel;

    /// <summary>Maps a Microsoft logging level onto the Serilog level scale.</summary>
    /// <param name="level">Level to map.</param>
    /// <returns>The equivalent Serilog level.</returns>
    public static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Fatal,
    };

    private static LogLevel ToMicrosoftLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };
}
