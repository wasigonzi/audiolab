using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Telemetry;

/// <summary>
/// What a measured trial of one setting concluded, on one machine, for one workload.
/// </summary>
/// <remarks>
/// The scope in the first three properties is the point of this record. A trial result is evidence
/// about the machine it was measured on and nothing else, and every query for one carries that
/// scope so a result can never be applied to hardware it was not measured on.
/// </remarks>
public sealed record TweakTrialResult
{
    /// <summary>Machine the trial ran on.</summary>
    public required string HardwareFingerprint { get; init; }

    /// <summary>Game or workload the trial measured.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>Module that was trialled.</summary>
    public required string TweakId { get; init; }

    /// <summary>
    /// Stable hash of the options used, so the same module with different options is a different
    /// trial rather than an overwrite of the previous one.
    /// </summary>
    public required string OptionsHash { get; init; }

    /// <summary>The options themselves, for display.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What the evaluator decided.</summary>
    public required TrialDecision Decision { get; init; }

    /// <summary>The explanation shown next to the numbers.</summary>
    public required string Rationale { get; init; }

    /// <summary>Baseline run this was measured against.</summary>
    public Guid? BaselineRunId { get; init; }

    /// <summary>Candidate run the decision was based on.</summary>
    public Guid? CandidateRunId { get; init; }

    /// <summary>Baseline 99th percentile frame time in milliseconds.</summary>
    public double? BaselineP99Ms { get; init; }

    /// <summary>Candidate 99th percentile frame time in milliseconds.</summary>
    public double? CandidateP99Ms { get; init; }

    /// <summary>Baseline mean frame time in milliseconds.</summary>
    public double? BaselineMeanMs { get; init; }

    /// <summary>Candidate mean frame time in milliseconds.</summary>
    public double? CandidateMeanMs { get; init; }

    /// <summary>Relative change in the metric the decision turned on.</summary>
    public double? RelativeChange { get; init; }

    /// <summary>Two sided p-value behind the decision, when one was computed.</summary>
    public double? PValue { get; init; }

    /// <summary>Frames in the candidate sample.</summary>
    public int SampleCount { get; init; }

    /// <summary>When the trial was evaluated.</summary>
    public required DateTimeOffset EvaluatedAtUtc { get; init; }

    /// <summary>How many times this exact trial has been run, across sessions.</summary>
    public int TrialCount { get; init; } = 1;
}
