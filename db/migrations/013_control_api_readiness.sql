CREATE TABLE IF NOT EXISTS entitysync.control_worker_heartbeats (
    worker_id text PRIMARY KEY,
    observed_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_control_worker_heartbeats_observed_at
    ON entitysync.control_worker_heartbeats (observed_at DESC);
