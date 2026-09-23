-- Velocity schema v2: auto-tune results.
--
-- A trial result answers one question: on THIS machine, for THIS workload, did THIS setting
-- measurably help? The primary key carries that scope, because a result that is not scoped that
-- way is not evidence about the machine in front of the user.
--
-- Options are hashed rather than stored as a document in the key, so "hardware scheduling on" and
-- "hardware scheduling off" are distinct trials of the same module, while the readable options
-- document stays available for the UI.

CREATE TABLE tweak_trial_result (
    hardware_fingerprint  TEXT    NOT NULL,
    workload_id           TEXT    NOT NULL,
    tweak_id              TEXT    NOT NULL,
    options_hash          TEXT    NOT NULL,
    options_document      TEXT    NOT NULL DEFAULT '{}',
    decision              INTEGER NOT NULL,
    rationale             TEXT    NOT NULL,
    baseline_run_id       TEXT    NULL,
    candidate_run_id      TEXT    NULL,
    baseline_p99_ms       REAL    NULL,
    candidate_p99_ms      REAL    NULL,
    baseline_mean_ms      REAL    NULL,
    candidate_mean_ms     REAL    NULL,
    relative_change       REAL    NULL,
    p_value               REAL    NULL,
    sample_count          INTEGER NOT NULL DEFAULT 0,
    evaluated_at_utc      TEXT    NOT NULL,
    trial_count           INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (hardware_fingerprint, workload_id, tweak_id, options_hash)
);

CREATE INDEX ix_trial_scope
    ON tweak_trial_result (hardware_fingerprint, workload_id, evaluated_at_utc DESC);

CREATE INDEX ix_trial_tweak
    ON tweak_trial_result (tweak_id, decision);
