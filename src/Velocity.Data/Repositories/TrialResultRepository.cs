using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Data.Repositories;

/// <summary>Stores what auto-tune has measured on this machine.</summary>
/// <remarks>
/// Every read is scoped by hardware fingerprint and workload. As with benchmark history, there is
/// deliberately no query that reaches across machines: a setting that helped on someone else's
/// hardware is not a reason to change this one.
/// </remarks>
public interface ITrialResultRepository
{
    /// <summary>
    /// Stores a trial result, replacing any previous result for the same scope and options and
    /// incrementing the count of how many times it has been measured.
    /// </summary>
    /// <param name="result">Result to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task UpsertAsync(TweakTrialResult result, CancellationToken cancellationToken);

    /// <summary>Reads every result for one machine and workload.</summary>
    /// <param name="hardwareFingerprint">Composite hardware fingerprint.</param>
    /// <param name="workloadId">Workload identifier.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The results, most recently evaluated first.</returns>
    Task<IReadOnlyList<TweakTrialResult>> GetResultsAsync(
        string hardwareFingerprint,
        string workloadId,
        CancellationToken cancellationToken);

    /// <summary>Reads one result.</summary>
    /// <param name="hardwareFingerprint">Composite hardware fingerprint.</param>
    /// <param name="workloadId">Workload identifier.</param>
    /// <param name="tweakId">Module identifier.</param>
    /// <param name="optionsHash">Hash of the options trialled.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The result, or <see langword="null"/> when this has not been measured.</returns>
    Task<TweakTrialResult?> GetResultAsync(
        string hardwareFingerprint,
        string workloadId,
        string tweakId,
        string optionsHash,
        CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="ITrialResultRepository"/>.</summary>
public sealed class TrialResultRepository : ITrialResultRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public TrialResultRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(TweakTrialResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tweak_trial_result
                (hardware_fingerprint, workload_id, tweak_id, options_hash, options_document,
                 decision, rationale, baseline_run_id, candidate_run_id,
                 baseline_p99_ms, candidate_p99_ms, baseline_mean_ms, candidate_mean_ms,
                 relative_change, p_value, sample_count, evaluated_at_utc, trial_count)
            VALUES
                ($fingerprint, $workload, $tweak, $hash, $options,
                 $decision, $rationale, $baselineRun, $candidateRun,
                 $baselineP99, $candidateP99, $baselineMean, $candidateMean,
                 $change, $pValue, $samples, $evaluated, 1)
            ON CONFLICT (hardware_fingerprint, workload_id, tweak_id, options_hash) DO UPDATE SET
                options_document = excluded.options_document,
                decision = excluded.decision,
                rationale = excluded.rationale,
                baseline_run_id = excluded.baseline_run_id,
                candidate_run_id = excluded.candidate_run_id,
                baseline_p99_ms = excluded.baseline_p99_ms,
                candidate_p99_ms = excluded.candidate_p99_ms,
                baseline_mean_ms = excluded.baseline_mean_ms,
                candidate_mean_ms = excluded.candidate_mean_ms,
                relative_change = excluded.relative_change,
                p_value = excluded.p_value,
                sample_count = excluded.sample_count,
                evaluated_at_utc = excluded.evaluated_at_utc,

                -- The count is how much evidence stands behind this answer, so it accumulates
                -- across sessions rather than resetting with each measurement.
                trial_count = tweak_trial_result.trial_count + 1;
            """;

        command.WithParameter("$fingerprint", result.HardwareFingerprint)
               .WithParameter("$workload", result.WorkloadId)
               .WithParameter("$tweak", result.TweakId)
               .WithParameter("$hash", result.OptionsHash)
               .WithParameter("$options", JsonSerializer.Serialize(result.Options, VelocityJson.Options))
               .WithParameter("$decision", (int)result.Decision)
               .WithParameter("$rationale", result.Rationale)
               .WithParameter("$baselineRun", SqliteValueConverter.ToTextOrNull(result.BaselineRunId))
               .WithParameter("$candidateRun", SqliteValueConverter.ToTextOrNull(result.CandidateRunId))
               .WithParameter("$baselineP99", result.BaselineP99Ms)
               .WithParameter("$candidateP99", result.CandidateP99Ms)
               .WithParameter("$baselineMean", result.BaselineMeanMs)
               .WithParameter("$candidateMean", result.CandidateMeanMs)
               .WithParameter("$change", result.RelativeChange)
               .WithParameter("$pValue", result.PValue)
               .WithParameter("$samples", result.SampleCount)
               .WithParameter("$evaluated", SqliteValueConverter.ToText(result.EvaluatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TweakTrialResult>> GetResultsAsync(
        string hardwareFingerprint,
        string workloadId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT hardware_fingerprint, workload_id, tweak_id, options_hash, options_document,
                   decision, rationale, baseline_run_id, candidate_run_id,
                   baseline_p99_ms, candidate_p99_ms, baseline_mean_ms, candidate_mean_ms,
                   relative_change, p_value, sample_count, evaluated_at_utc, trial_count
            FROM tweak_trial_result
            WHERE hardware_fingerprint = $fingerprint AND workload_id = $workload
            ORDER BY evaluated_at_utc DESC;
            """;
        command.WithParameter("$fingerprint", hardwareFingerprint)
               .WithParameter("$workload", workloadId);

        var results = new List<TweakTrialResult>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<TweakTrialResult?> GetResultAsync(
        string hardwareFingerprint,
        string workloadId,
        string tweakId,
        string optionsHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT hardware_fingerprint, workload_id, tweak_id, options_hash, options_document,
                   decision, rationale, baseline_run_id, candidate_run_id,
                   baseline_p99_ms, candidate_p99_ms, baseline_mean_ms, candidate_mean_ms,
                   relative_change, p_value, sample_count, evaluated_at_utc, trial_count
            FROM tweak_trial_result
            WHERE hardware_fingerprint = $fingerprint
              AND workload_id = $workload
              AND tweak_id = $tweak
              AND options_hash = $hash;
            """;
        command.WithParameter("$fingerprint", hardwareFingerprint)
               .WithParameter("$workload", workloadId)
               .WithParameter("$tweak", tweakId)
               .WithParameter("$hash", optionsHash);

        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static TweakTrialResult Read(SqliteDataReader reader) => new()
    {
        HardwareFingerprint = reader.GetString(0),
        WorkloadId = reader.GetString(1),
        TweakId = reader.GetString(2),
        OptionsHash = reader.GetString(3),
        Options = JsonSerializer.Deserialize<Dictionary<string, string>>(
                      reader.GetString(4), VelocityJson.Options)
                  ?? new Dictionary<string, string>(StringComparer.Ordinal),
        Decision = (TrialDecision)reader.GetInt32(5),
        Rationale = reader.GetString(6),
        BaselineRunId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
        CandidateRunId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
        BaselineP99Ms = reader.IsDBNull(9) ? null : reader.GetDouble(9),
        CandidateP99Ms = reader.IsDBNull(10) ? null : reader.GetDouble(10),
        BaselineMeanMs = reader.IsDBNull(11) ? null : reader.GetDouble(11),
        CandidateMeanMs = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        RelativeChange = reader.IsDBNull(13) ? null : reader.GetDouble(13),
        PValue = reader.IsDBNull(14) ? null : reader.GetDouble(14),
        SampleCount = reader.GetInt32(15),
        EvaluatedAtUtc = reader.GetTimestamp(16),
        TrialCount = reader.GetInt32(17),
    };
}
