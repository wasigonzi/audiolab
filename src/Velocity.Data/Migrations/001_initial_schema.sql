-- Velocity schema v1.
--
-- Conventions:
--   * Every timestamp is stored as an ISO-8601 string with offset, in UTC.
--   * Every enum is stored as its integer value; the C# enums are append-only.
--   * Identifiers are GUIDs in "D" format (lower case, hyphenated).
--   * The schema_version table is created by the migrator itself, not by a migration, so that a
--     migration set is never responsible for its own bookkeeping table.
--   * Large or evolving structures (a captured system profile, profile tweak settings) are stored
--     as JSON documents alongside the indexed columns needed to query them, so that adding a field
--     to a model does not require a schema migration.

-- ---------------------------------------------------------------------------
-- Hardware identity
-- ---------------------------------------------------------------------------

CREATE TABLE system_profile (
    id                     TEXT    NOT NULL PRIMARY KEY,
    captured_at_utc        TEXT    NOT NULL,
    fingerprint_composite  TEXT    NOT NULL,
    fingerprint_cpu        TEXT    NOT NULL,
    fingerprint_gpu        TEXT    NOT NULL,
    fingerprint_memory     TEXT    NOT NULL,
    fingerprint_os         TEXT    NOT NULL,
    os_build               INTEGER NOT NULL,
    cpu_brand              TEXT    NOT NULL,
    document               TEXT    NOT NULL
);

CREATE INDEX ix_system_profile_fingerprint
    ON system_profile (fingerprint_composite, captured_at_utc DESC);

-- ---------------------------------------------------------------------------
-- Transaction journal. This is the crash recovery record: a transaction row is
-- committed before any system state is written, and its status column is what the
-- recovery service inspects on the next launch.
-- ---------------------------------------------------------------------------

CREATE TABLE optimization_transaction (
    id                    TEXT    NOT NULL PRIMARY KEY,
    status                INTEGER NOT NULL,
    reason                INTEGER NOT NULL,
    profile_id            TEXT    NULL,
    session_id            TEXT    NULL,
    hardware_fingerprint  TEXT    NOT NULL,
    started_at_utc        TEXT    NOT NULL,
    completed_at_utc      TEXT    NULL
);

CREATE INDEX ix_transaction_status ON optimization_transaction (status);
CREATE INDEX ix_transaction_started ON optimization_transaction (started_at_utc DESC);

CREATE TABLE state_snapshot (
    id               TEXT NOT NULL PRIMARY KEY,
    transaction_id   TEXT NOT NULL REFERENCES optimization_transaction (id) ON DELETE CASCADE,
    tweak_id         TEXT NOT NULL,
    captured_at_utc  TEXT NOT NULL
);

CREATE INDEX ix_snapshot_transaction ON state_snapshot (transaction_id);
CREATE INDEX ix_snapshot_tweak ON state_snapshot (tweak_id, captured_at_utc DESC);

-- state_key holds the canonical "provider://path#item" form and is part of the primary key.
-- The provider/path/item columns are duplicated for querying ("show me everything this product
-- changed under HKLM\SYSTEM") because item is nullable and SQLite permits NULLs in primary keys.
CREATE TABLE state_snapshot_entry (
    snapshot_id  TEXT    NOT NULL REFERENCES state_snapshot (id) ON DELETE CASCADE,
    state_key    TEXT    NOT NULL,
    provider     TEXT    NOT NULL,
    path         TEXT    NOT NULL,
    item         TEXT    NULL,
    value_kind   INTEGER NOT NULL,
    value_data   TEXT    NULL,
    PRIMARY KEY (snapshot_id, state_key)
);

CREATE INDEX ix_snapshot_entry_key ON state_snapshot_entry (provider, path);

CREATE TABLE transaction_step (
    id                    TEXT    NOT NULL PRIMARY KEY,
    transaction_id        TEXT    NOT NULL REFERENCES optimization_transaction (id) ON DELETE CASCADE,
    ordinal               INTEGER NOT NULL,
    tweak_id              TEXT    NOT NULL,
    tweak_version         INTEGER NOT NULL,
    snapshot_id           TEXT    NULL REFERENCES state_snapshot (id),
    apply_outcome         INTEGER NULL,
    verification_status   INTEGER NULL,
    message               TEXT    NULL,
    rollback_payload      TEXT    NULL,
    started_at_utc        TEXT    NOT NULL,
    completed_at_utc      TEXT    NULL,
    rolled_back           INTEGER NOT NULL DEFAULT 0,
    UNIQUE (transaction_id, ordinal)
);

CREATE INDEX ix_step_tweak ON transaction_step (tweak_id);

-- ---------------------------------------------------------------------------
-- What is currently applied. Distinguishes "this product changed it" from
-- "the machine was already like that", which the Restore Center depends on.
-- ---------------------------------------------------------------------------

CREATE TABLE applied_tweak (
    tweak_id            TEXT    NOT NULL PRIMARY KEY,
    transaction_id      TEXT    NOT NULL REFERENCES optimization_transaction (id) ON DELETE CASCADE,
    definition_version  INTEGER NOT NULL,
    scope               INTEGER NOT NULL,
    applied_at_utc      TEXT    NOT NULL
);

-- ---------------------------------------------------------------------------
-- Profiles
-- ---------------------------------------------------------------------------

CREATE TABLE optimization_profile (
    id              TEXT    NOT NULL PRIMARY KEY,
    name            TEXT    NOT NULL,
    kind            INTEGER NOT NULL,
    game_id         TEXT    NULL,
    is_built_in     INTEGER NOT NULL DEFAULT 0,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    created_at_utc  TEXT    NOT NULL,
    modified_at_utc TEXT    NOT NULL,
    document        TEXT    NOT NULL
);

CREATE INDEX ix_profile_game ON optimization_profile (game_id);

-- ---------------------------------------------------------------------------
-- Audit log
-- ---------------------------------------------------------------------------

CREATE TABLE audit_log (
    id              TEXT    NOT NULL PRIMARY KEY,
    timestamp_utc   TEXT    NOT NULL,
    module          TEXT    NOT NULL,
    action          TEXT    NOT NULL,
    target          TEXT    NULL,
    original_value  TEXT    NULL,
    new_value       TEXT    NULL,
    outcome         INTEGER NOT NULL,
    transaction_id  TEXT    NULL,
    error           TEXT    NULL,
    details         TEXT    NULL
);

CREATE INDEX ix_audit_timestamp ON audit_log (timestamp_utc DESC);
CREATE INDEX ix_audit_transaction ON audit_log (transaction_id);

-- ---------------------------------------------------------------------------
-- Benchmark history. Results are only ever compared within one
-- (hardware_fingerprint, workload_id) pair.
-- ---------------------------------------------------------------------------

CREATE TABLE benchmark_run (
    id                     TEXT    NOT NULL PRIMARY KEY,
    hardware_fingerprint   TEXT    NOT NULL,
    workload_id            TEXT    NOT NULL,
    transaction_id         TEXT    NULL,
    label                  TEXT    NOT NULL,
    started_at_utc         TEXT    NOT NULL,
    sample_count           INTEGER NOT NULL DEFAULT 0,
    duration_ms            REAL    NOT NULL DEFAULT 0,
    mean_frame_time_ms     REAL    NULL,
    median_frame_time_ms   REAL    NULL,
    p95_frame_time_ms      REAL    NULL,
    p99_frame_time_ms      REAL    NULL,
    p999_frame_time_ms     REAL    NULL,
    stddev_frame_time_ms   REAL    NULL,
    mean_cpu_utilization   REAL    NULL,
    mean_background_cpu    REAL    NULL,
    mean_gpu_utilization   REAL    NULL,
    mean_committed_bytes   INTEGER NULL,
    mean_disk_bytes_sec    REAL    NULL,
    mean_network_latency   REAL    NULL,
    network_jitter_ms      REAL    NULL,
    applied_tweaks         TEXT    NOT NULL DEFAULT '[]'
);

CREATE INDEX ix_benchmark_scope
    ON benchmark_run (hardware_fingerprint, workload_id, started_at_utc DESC);

-- ---------------------------------------------------------------------------
-- Key/value settings
-- ---------------------------------------------------------------------------

CREATE TABLE setting (
    key             TEXT NOT NULL PRIMARY KEY,
    value           TEXT NOT NULL,
    updated_at_utc  TEXT NOT NULL
);
