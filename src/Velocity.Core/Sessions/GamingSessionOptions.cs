using System;

namespace Velocity.Core.Sessions;

/// <summary>Timings and limits for a gaming session.</summary>
public sealed record GamingSessionOptions
{
    /// <summary>
    /// How long to wait for a game started through a store launcher to appear.
    /// </summary>
    /// <remarks>
    /// Store launchers update themselves, show news pages and run anti-cheat installers before the
    /// game starts, so this is minutes rather than seconds. When it expires the session restores
    /// everything and reports that the game never appeared, rather than leaving the machine
    /// optimized for a game that is not running.
    /// </remarks>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to check whether the game is still running.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the game must be absent before the session treats it as ended.
    /// </summary>
    /// <remarks>
    /// Some titles restart themselves once at startup, to switch to a different renderer or to
    /// relaunch under their anti-cheat. Ending the session on the first missed poll would restore
    /// the machine seconds after the user started playing.
    /// </remarks>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(20);
}
