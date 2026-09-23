using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Core.AutoTune;

/// <summary>One setting, with one set of options, to trial.</summary>
/// <param name="TweakId">Module to trial.</param>
/// <param name="Options">Options to pass to it.</param>
/// <param name="Description">What this candidate is trying, for the report.</param>
public sealed record AutoTuneCandidate(
    string TweakId,
    IReadOnlyDictionary<string, string> Options,
    string Description)
{
    /// <summary>
    /// Stable hash of the options, so the same module with different options is a separate trial.
    /// </summary>
    /// <returns>The hash.</returns>
    public string OptionsHash() => AutoTuneHash.ForOptions(Options);
}

/// <summary>Hashes a candidate's options into a stable key.</summary>
/// <remarks>
/// Keys are sorted before hashing so that two dictionaries with the same content hash the same
/// whatever order they were built in. Without that, re-running the same trial would create a
/// second row instead of accumulating evidence on the first.
/// </remarks>
public static class AutoTuneHash
{
    /// <summary>Hashes one option set.</summary>
    /// <param name="options">Options to hash.</param>
    /// <returns>A short hexadecimal hash.</returns>
    public static string ForOptions(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return "default";
        }

        string canonical = string.Join(
            ';',
            options
                .OrderBy(option => option.Key, StringComparer.Ordinal)
                .Select(option => $"{option.Key}={option.Value}"));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}

/// <summary>What to tune, and how carefully.</summary>
public sealed record AutoTuneRequest
{
    /// <summary>Game or workload being tuned; every result is scoped to it.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>Process to capture frames from, or <c>0</c> for every process.</summary>
    public int ProcessId { get; init; }

    /// <summary>Candidates to trial, in order.</summary>
    public required IReadOnlyList<AutoTuneCandidate> Candidates { get; init; }

    /// <summary>Profile intent, which decides which metric a verdict turns on.</summary>
    public ProfileKind ProfileKind { get; init; } = ProfileKind.Balanced;

    /// <summary>How long each measurement runs.</summary>
    public TimeSpan MeasurementDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Re-measure the baseline before every candidate rather than once at the start.
    /// </summary>
    /// <remarks>
    /// A machine drifts over a tuning run: it warms up, a background task starts, the game reaches
    /// a different area. Re-measuring costs time and is the only way to keep a late candidate from
    /// being judged against a baseline taken under different conditions.
    /// </remarks>
    public bool RebaselineBeforeEachCandidate { get; init; } = true;

    /// <summary>
    /// Skip a candidate already measured on this machine and workload.
    /// </summary>
    public bool SkipAlreadyMeasured { get; init; } = true;
}

/// <summary>What happened to one candidate.</summary>
/// <param name="Candidate">The candidate.</param>
/// <param name="Outcome">What the engine did with it.</param>
/// <param name="Result">The stored trial result, when the candidate was measured.</param>
/// <param name="Detail">What to show the user.</param>
public sealed record AutoTuneCandidateReport(
    AutoTuneCandidate Candidate,
    AutoTuneOutcome Outcome,
    TweakTrialResult? Result,
    string Detail);

/// <summary>What the engine did with a candidate.</summary>
public enum AutoTuneOutcome
{
    /// <summary>Measured, found to help, and left applied.</summary>
    Kept = 0,

    /// <summary>Measured and reverted: it did not help, or it hurt the tail.</summary>
    Reverted = 1,

    /// <summary>Not applicable to this machine, so never applied.</summary>
    Incompatible = 2,

    /// <summary>Already measured on this machine and workload; the stored answer stands.</summary>
    AlreadyMeasured = 3,

    /// <summary>Could not be measured, so no conclusion was drawn and nothing was kept.</summary>
    NotMeasured = 4,

    /// <summary>The run was stopped before this candidate was reached.</summary>
    NotReached = 5,
}

/// <summary>The outcome of a whole tuning run.</summary>
public sealed record AutoTuneReport
{
    /// <summary>Workload that was tuned.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>Machine the run measured.</summary>
    public required string HardwareFingerprint { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When the run finished.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Per candidate outcomes, in the order they were trialled.</summary>
    public IReadOnlyList<AutoTuneCandidateReport> Candidates { get; init; } = [];

    /// <summary>Why the run stopped early, when it did.</summary>
    public string? StoppedBecause { get; init; }

    /// <summary>Candidates that measured as an improvement and were left applied.</summary>
    public IEnumerable<AutoTuneCandidateReport> Kept =>
        Candidates.Where(candidate => candidate.Outcome == AutoTuneOutcome.Kept);
}
