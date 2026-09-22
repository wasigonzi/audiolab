using Microsoft.Extensions.Logging;

namespace Velocity.Diagnostics.Logging;

/// <summary>
/// Changes the active log level at runtime without rebuilding the logging pipeline.
/// </summary>
/// <remarks>
/// Used by the gaming session manager to quieten logging while a game is running, and to restore
/// the configured level when the session ends.
/// </remarks>
public interface ILogVerbosityController
{
    /// <summary>The level currently being written.</summary>
    LogLevel CurrentLevel { get; }

    /// <summary>Raises the minimum level for the duration of a gaming session.</summary>
    void EnterGamingSession();

    /// <summary>Restores the configured minimum level.</summary>
    void LeaveGamingSession();
}
