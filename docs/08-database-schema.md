# 8. Database schema

SQLite at `%ProgramData%\Velocity\velocity.db`, WAL journal mode, `synchronous = FULL`,
`busy_timeout = 5000`, foreign keys enabled per connection.

Two processes touch this file (desktop and helper), which is why WAL and a busy timeout are used
rather than a simple rollback journal. `foreign_keys` is a per-connection pragma in SQLite, not a
database property, so `SqliteConnectionFactory` sets it on every connection — otherwise the cascade
deletes that tie snapshots to their transaction silently do nothing. A test asserts this.

## Conventions

- Timestamps: ISO-8601 with offset, UTC, fixed format, so ordering by timestamp is a string compare.
- Enums: stored as integers; the C# enums are append-only.
- Identifiers: GUIDs in `D` format.
- Large or evolving structures (a captured `SystemProfile`, a profile's tweak settings) are stored
  as a JSON document alongside the columns needed to query them, so adding a probe field does not
  require a migration.

## Migrations

`schema_version (version, description, applied_at_utc)` is created by the migrator itself, not by a
migration, so a migration set is never responsible for its own bookkeeping table. Migrations are
embedded `NNN_description.sql` resources, ordered by numeric prefix. Each migration and its version
row commit together, so an interrupted upgrade leaves the database at the last fully applied
version — covered by `DatabaseMigratorTests`.

## Tables (v1)

### Hardware identity

```sql
system_profile(
  id, captured_at_utc,
  fingerprint_composite, fingerprint_cpu, fingerprint_gpu, fingerprint_memory, fingerprint_os,
  os_build, cpu_brand,
  document)                                  -- full SystemProfile as JSON
  INDEX (fingerprint_composite, captured_at_utc DESC)
```

### Transaction journal — the crash-recovery record

```sql
optimization_transaction(
  id PK, status, reason, profile_id, session_id,
  hardware_fingerprint, started_at_utc, completed_at_utc)
  INDEX (status), INDEX (started_at_utc DESC)

state_snapshot(
  id PK, transaction_id → optimization_transaction ON DELETE CASCADE,
  tweak_id, captured_at_utc)

state_snapshot_entry(
  snapshot_id → state_snapshot ON DELETE CASCADE,
  state_key,                                  -- canonical provider://path#item
  provider, path, item,                       -- duplicated for querying
  value_kind, value_data,
  PRIMARY KEY (snapshot_id, state_key))

transaction_step(
  id PK, transaction_id → … ON DELETE CASCADE, ordinal,
  tweak_id, tweak_version, snapshot_id → state_snapshot,
  apply_outcome, verification_status, message, rollback_payload,
  started_at_utc, completed_at_utc, rolled_back,
  UNIQUE (transaction_id, ordinal))
```

`state_key` is part of the primary key rather than `(provider, path, item)` because `item` is
nullable and SQLite permits NULLs in primary key columns, which would break uniqueness. The
component columns are kept for queries like "everything this product changed under `HKLM\SYSTEM`".

### What is currently applied

```sql
applied_tweak(
  tweak_id PK, transaction_id → …, definition_version, scope, applied_at_utc)
```

This table is what lets the Restore Center distinguish *"the machine happens to be configured that
way"* from *"we configured it that way"* — the difference between an honest restore and overwriting
the user's own settings with a guessed default.

### Profiles, audit, benchmarks, settings

```sql
optimization_profile(
  id PK, name, kind, game_id, is_built_in, schema_version,
  created_at_utc, modified_at_utc, document)
  INDEX (game_id)

audit_log(
  id PK, timestamp_utc, module, action, target,
  original_value, new_value, outcome, transaction_id, error, details)
  INDEX (timestamp_utc DESC), INDEX (transaction_id)

benchmark_run(
  id PK, hardware_fingerprint, workload_id, transaction_id, label, started_at_utc,
  sample_count, duration_ms,
  mean_frame_time_ms, median_frame_time_ms,
  p95_frame_time_ms, p99_frame_time_ms, p999_frame_time_ms, stddev_frame_time_ms,
  mean_cpu_utilization, mean_background_cpu, mean_gpu_utilization,
  mean_committed_bytes, mean_disk_bytes_sec, mean_network_latency, network_jitter_ms,
  applied_tweaks)
  INDEX (hardware_fingerprint, workload_id, started_at_utc DESC)

setting(key PK, value, updated_at_utc)
```

`benchmark_run` stores frame **times**, with frame rates derived for display. Averaging frames per
second hides exactly the stutter this product exists to remove.

`IBenchmarkRepository` deliberately exposes no "average across all machines" query. A result
measured on other hardware is not evidence about this machine, and the auto-tune engine must not be
able to reach one — a test asserts the scoping.

## Retention

Journal rows are kept indefinitely by default: they are the record of what was changed on the user's
machine, and the Restore Center depends on them. Trimming, when it arrives, will be explicit,
user-visible, and will never remove a transaction that has not been rolled back.
