using System;
using System.Collections.Generic;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Abstractions.Games;

/// <summary>Stage a gaming session has reached.</summary>
public enum GameSessionState
{
    /// <summary>Created but not started.</summary>
    Created = 0,

    /// <summary>Optimizations are being applied.</summary>
    Optimizing = 1,

    /// <summary>The game is being started.</summary>
    Launching = 2,

    /// <summary>The game is running and telemetry is being collected.</summary>
    Running = 3,

    /// <summary>The game has exited and the captured state is being restored.</summary>
    Restoring = 4,

    /// <summary>Finished, with everything restored.</summary>
    Completed = 5,

    /// <summary>
    /// Finished, but at least one optimization could not be restored. This state exists so the
    /// product never quietly leaves a machine changed.
    /// </summary>
    CompletedWithRestoreFailures = 6,

    /// <summary>The session never started, because optimization or launch failed.</summary>
    Failed = 7,
}

/// <summary>What happened during one gaming session.</summary>
public sealed record GameSessionReport
{
    /// <summary>Session identifier.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Game the session was for, when one was known.</summary>
    public string? GameId { get; init; }

    /// <summary>Game display name, or the executable name when nothing better is known.</summary>
    public required string GameName { get; init; }

    /// <summary>Profile that was applied, when one was.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Final state.</summary>
    public required GameSessionState State { get; init; }

    /// <summary>When the session started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When the session ended, or null while it is running.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>How long the game itself ran.</summary>
    public TimeSpan PlayTime { get; init; }

    /// <summary>Transaction holding the applied optimizations, when any were applied.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>Tweaks that were actually applied, as opposed to offered.</summary>
    public IReadOnlyList<string> AppliedTweakIds { get; init; } = Array.Empty<string>();

    /// <summary>Tweaks that were skipped, with the reason.</summary>
    public IReadOnlyDictionary<string, string> SkippedTweaks { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Resource telemetry observed while the game ran, when a monitor was attached.
    /// </summary>
    /// <remarks>
    /// These are system counters, not frame times. A session report does not claim a frame rate
    /// improvement: only a benchmark comparison against a before measurement can support that
    /// claim, which is what the Benchmark Lab is for.
    /// </remarks>
    public ResourceStatistics? Telemetry { get; init; }

    /// <summary>Number of telemetry samples behind <see cref="Telemetry"/>.</summary>
    public int TelemetrySampleCount { get; init; }

    /// <summary>What went wrong, when something did.</summary>
    public string? Error { get; init; }

    /// <summary>Optimizations that could not be restored, with the reason.</summary>
    public IReadOnlyDictionary<string, string> RestoreFailures { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
