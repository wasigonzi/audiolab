using Microsoft.Extensions.Logging;

namespace Velocity.Diagnostics.Logging;

/// <summary>Configuration for the logging pipeline.</summary>
public sealed class LoggingOptions
{
    /// <summary>Minimum level written when no gaming session is active.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Minimum level while a gaming session is active.
    /// </summary>
    /// <remarks>
    /// Raised by default because the optimizer must not become a source of disk activity during
    /// the session it is supposed to be protecting.
    /// </remarks>
    public LogLevel GamingSessionMinimumLevel { get; set; } = LogLevel.Warning;

    /// <summary>Whether to also write to the console. Off for the desktop UI, on for the CLI.</summary>
    public bool WriteToConsole { get; set; }

    /// <summary>Number of daily log files to keep.</summary>
    public int RetainedFileCount { get; set; } = 14;

    /// <summary>Maximum size of a single log file in bytes.</summary>
    public long FileSizeLimitBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Log file name prefix. The date is appended by the rolling file sink.</summary>
    public string FileNamePrefix { get; set; } = "velocity";
}
