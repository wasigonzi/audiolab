using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Velocity.Abstractions.Telemetry;

namespace Velocity.Data.Repositories;

/// <summary>Stores and queries benchmark history.</summary>
/// <remarks>
/// Queries are always scoped by hardware fingerprint and workload. There is deliberately no
/// "average result across all users" query in this interface: a result measured on other hardware
/// is not evidence about this machine, and the auto-tune engine must not be able to reach one.
/// </remarks>
public interface IBenchmarkRepository
{
    /// <summary>Stores a benchmark run.</summary>
    /// <param name="run">Run to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken);

    /// <summary>Reads runs for one machine and workload, newest first.</summary>
    /// <param name="hardwareFingerprint">Composite hardware fingerprint.</param>
    /// <param name="workloadId">Workload or game identifier.</param>
    /// <param name="limit">Maximum number of runs to return.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The runs.</returns>
    Task<IReadOnlyList<BenchmarkRun>> GetRunsAsync(
        string hardwareFingerprint,
        string workloadId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>SQLite implementation of <see cref="IBenchmarkRepository"/>.</summary>
public sealed class BenchmarkRepository : IBenchmarkRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public BenchmarkRepository(IDatabaseConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO benchmark_run
                (id, hardware_fingerprint, workload_id, transaction_id, label, started_at_utc,
                 sample_count, duration_ms, mean_frame_time_ms, median_frame_time_ms,
                 p95_frame_time_ms, p99_frame_time_ms, p999_frame_time_ms, stddev_frame_time_ms,
                 mean_cpu_utilization, mean_background_cpu, mean_gpu_utilization,
                 mean_committed_bytes, mean_disk_bytes_sec, mean_network_latency,
                 network_jitter_ms, applied_tweaks)
            VALUES
                ($id, $fingerprint, $workload, $transaction, $label, $started,
                 $samples, $duration, $mean, $median, $p95, $p99, $p999, $stddev,
                 $cpu, $backgroundCpu, $gpu, $committed, $disk, $latency, $jitter, $tweaks);
            """;

        FrameTimeStatistics? frames = run.FrameTimes;
        command.WithParameter("$id", SqliteValueConverter.ToText(run.Id))
               .WithParameter("$fingerprint", run.HardwareFingerprint)
               .WithParameter("$workload", run.WorkloadId)
               .WithParameter("$transaction", SqliteValueConverter.ToTextOrNull(run.TransactionId))
               .WithParameter("$label", run.Label)
               .WithParameter("$started", SqliteValueConverter.ToText(run.StartedAtUtc))
               .WithParameter("$samples", frames?.SampleCount ?? 0)
               .WithParameter("$duration", frames?.Duration.TotalMilliseconds ?? 0d)
               .WithParameter("$mean", frames?.MeanFrameTimeMs)
               .WithParameter("$median", frames?.MedianFrameTimeMs)
               .WithParameter("$p95", frames?.P95FrameTimeMs)
               .WithParameter("$p99", frames?.P99FrameTimeMs)
               .WithParameter("$p999", frames?.P999FrameTimeMs)
               .WithParameter("$stddev", frames?.StandardDeviationMs)
               .WithParameter("$cpu", run.Resources.MeanCpuUtilization)
               .WithParameter("$backgroundCpu", run.Resources.MeanBackgroundCpuUtilization)
               .WithParameter("$gpu", run.Resources.MeanGpuUtilization)
               .WithParameter("$committed", run.Resources.MeanCommittedBytes)
               .WithParameter("$disk", run.Resources.MeanDiskBytesPerSecond)
               .WithParameter("$latency", run.Resources.MeanNetworkLatencyMs)
               .WithParameter("$jitter", run.Resources.NetworkJitterMs)
               .WithParameter("$tweaks", JsonSerializer.Serialize(run.AppliedTweakIds, VelocityJson.Options));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BenchmarkRun>> GetRunsAsync(
        string hardwareFingerprint,
        string workloadId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, hardware_fingerprint, workload_id, transaction_id, label, started_at_utc,
                   sample_count, duration_ms, mean_frame_time_ms, median_frame_time_ms,
                   p95_frame_time_ms, p99_frame_time_ms, p999_frame_time_ms, stddev_frame_time_ms,
                   mean_cpu_utilization, mean_background_cpu, mean_gpu_utilization,
                   mean_committed_bytes, mean_disk_bytes_sec, mean_network_latency,
                   network_jitter_ms, applied_tweaks
              FROM benchmark_run
             WHERE hardware_fingerprint = $fingerprint AND workload_id = $workload
             ORDER BY started_at_utc DESC
             LIMIT {limit.ToString(CultureInfo.InvariantCulture)};
            """;
        command.WithParameter("$fingerprint", hardwareFingerprint)
               .WithParameter("$workload", workloadId);

        var runs = new List<BenchmarkRun>();
        using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int sampleCount = reader.GetInt32(6);
            FrameTimeStatistics? frames = sampleCount == 0 || reader.IsDBNull(8)
                ? null
                : new FrameTimeStatistics
                {
                    SampleCount = sampleCount,
                    Duration = TimeSpan.FromMilliseconds(reader.GetDouble(7)),
                    MeanFrameTimeMs = reader.GetDouble(8),
                    MedianFrameTimeMs = reader.GetDouble(9),
                    P95FrameTimeMs = reader.GetDouble(10),
                    P99FrameTimeMs = reader.GetDouble(11),
                    P999FrameTimeMs = reader.GetDouble(12),
                    StandardDeviationMs = reader.GetDouble(13),
                };

            runs.Add(new BenchmarkRun
            {
                Id = reader.GetGuid(0),
                HardwareFingerprint = reader.GetString(1),
                WorkloadId = reader.GetString(2),
                TransactionId = reader.GetNullableGuid(3),
                Label = reader.GetString(4),
                StartedAtUtc = reader.GetTimestamp(5),
                FrameTimes = frames,
                Resources = new ResourceStatistics
                {
                    MeanCpuUtilization = reader.GetNullableDouble(14) ?? 0d,
                    MeanBackgroundCpuUtilization = reader.GetNullableDouble(15) ?? 0d,
                    MeanGpuUtilization = reader.GetNullableDouble(16) ?? 0d,
                    MeanCommittedBytes = reader.GetNullableInt64(17) ?? 0L,
                    MeanDiskBytesPerSecond = reader.GetNullableDouble(18) ?? 0d,
                    MeanNetworkLatencyMs = reader.GetNullableDouble(19),
                    NetworkJitterMs = reader.GetNullableDouble(20),
                },
                AppliedTweakIds =
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(21), VelocityJson.Options)
                    ?? new List<string>(),
            });
        }

        return runs;
    }
}
