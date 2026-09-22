using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Telemetry;

/// <summary>Direction that counts as an improvement for a metric.</summary>
public enum MetricPolarity
{
    /// <summary>Larger values are better (frame rate).</summary>
    HigherIsBetter = 0,

    /// <summary>Smaller values are better (frame time, latency, jitter, background CPU).</summary>
    LowerIsBetter = 1,
}

/// <summary>Whether a measured difference is credible.</summary>
public enum SignificanceVerdict
{
    /// <summary>The difference is within measurement noise.</summary>
    NoDifference = 0,

    /// <summary>A credible improvement.</summary>
    Improvement = 1,

    /// <summary>A credible regression.</summary>
    Regression = 2,

    /// <summary>Not enough samples to decide.</summary>
    Inconclusive = 3,
}

/// <summary>Comparison of one metric between a baseline and a candidate run.</summary>
public sealed record MetricComparison
{
    /// <summary>Metric name, for example <c>p99_frame_time_ms</c>.</summary>
    public required string MetricName { get; init; }

    /// <summary>Which direction is an improvement.</summary>
    public required MetricPolarity Polarity { get; init; }

    /// <summary>Baseline value.</summary>
    public required double BaselineValue { get; init; }

    /// <summary>Candidate value.</summary>
    public required double CandidateValue { get; init; }

    /// <summary>Relative change from baseline to candidate, as a fraction.</summary>
    public double RelativeChange => Math.Abs(BaselineValue) < double.Epsilon
        ? 0d
        : (CandidateValue - BaselineValue) / BaselineValue;

    /// <summary>Two sided p-value from the significance test, when one was run.</summary>
    public double? PValue { get; init; }

    /// <summary>Lower bound of the confidence interval on the difference, when computed.</summary>
    public double? ConfidenceIntervalLow { get; init; }

    /// <summary>Upper bound of the confidence interval on the difference, when computed.</summary>
    public double? ConfidenceIntervalHigh { get; init; }

    /// <summary>Whether the difference is credible, and in which direction.</summary>
    public required SignificanceVerdict Verdict { get; init; }
}

/// <summary>Whether a candidate configuration should be kept.</summary>
public enum TrialDecision
{
    /// <summary>Keep the change.</summary>
    Keep = 0,

    /// <summary>Revert the change.</summary>
    Revert = 1,

    /// <summary>Neither; gather more samples.</summary>
    NeedsMoreData = 2,
}

/// <summary>The full verdict on one candidate configuration.</summary>
public sealed record TrialVerdict
{
    /// <summary>Decision the auto-tune engine reached.</summary>
    public required TrialDecision Decision { get; init; }

    /// <summary>Plain language explanation shown next to the numbers.</summary>
    public required string Rationale { get; init; }

    /// <summary>Per metric comparisons the decision was based on.</summary>
    public required IReadOnlyList<MetricComparison> Comparisons { get; init; }
}
