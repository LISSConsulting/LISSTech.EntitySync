-- Durable refresh state, lease-safe scheduler, snapshot boundary, atomic event receipts.
-- Idempotency key: entitysync.entity_refresh_events.event_id (client-supplied UUID).

CREATE TABLE IF NOT EXISTS entitysync.entity_refresh_state (
    tenant_id text NOT NULL,
    connection_id text NOT NULL,
    vendor text NOT NULL,
    connection_generation bigint NOT NULL CHECK (connection_generation > 0),
    entity_type text NOT NULL,
    status text NOT NULL
        CHECK (status IN ('Pending','Running','Succeeded','Failed')),
    mode text NOT NULL
        CHECK (mode IN ('Scheduled','Manual','Incremental')),
    last_attempt_at timestamptz,
    last_successful_at timestamptz,
    next_scheduled_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    observed_count bigint NOT NULL DEFAULT 0 CHECK (observed_count >= 0),
    cursor text,
    source_updated_at timestamptz,
    error_code text,
    snapshot_started_at timestamptz,
    snapshot_completed_at timestamptz,
    is_stale boolean NOT NULL DEFAULT false,
    refreshed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    lease_owner text,
    lease_expires_at timestamptz,
    PRIMARY KEY (tenant_id, connection_id, entity_type),
    CONSTRAINT entity_refresh_state_connection_fkey
        FOREIGN KEY (tenant_id, connection_id)
        REFERENCES entitysync.connection_definitions (tenant_id, connection_id)
        ON DELETE CASCADE,
    CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CHECK ((status = 'Running') = (lease_owner IS NOT NULL)),
    -- One-way implication: Running/Succeeded/Failed imply last_attempt_at is set;
    -- Pending may retain an attempt timestamp (e.g. previously attempted row)
    -- or omit it (e.g. freshly discovered readable type).
    CHECK (status = 'Pending'
        OR last_attempt_at IS NOT NULL),
    CHECK (snapshot_completed_at IS NULL OR snapshot_started_at IS NOT NULL),
    CHECK (snapshot_completed_at IS NULL OR snapshot_completed_at >= snapshot_started_at)
);

CREATE INDEX IF NOT EXISTS entity_refresh_state_lease_idx
    ON entitysync.entity_refresh_state (tenant_id, status, lease_expires_at);
-- Recurring scans must hit the due index for Succeeded rows too (LeaseDue selects
-- Succeeded rows whose next_scheduled_at has elapsed).
CREATE INDEX IF NOT EXISTS entity_refresh_state_due_idx
    ON entitysync.entity_refresh_state (next_scheduled_at)
    WHERE status IN ('Pending','Failed','Succeeded');
CREATE INDEX IF NOT EXISTS entity_refresh_state_stale_idx
    ON entitysync.entity_refresh_state (tenant_id, is_stale, next_scheduled_at);

CREATE TABLE IF NOT EXISTS entitysync.entity_refresh_events (
    tenant_id text NOT NULL,
    event_id uuid NOT NULL,
    connection_id text NOT NULL,
    vendor text NOT NULL,
    entity_type text NOT NULL,
    mode text NOT NULL
        CHECK (mode IN ('Scheduled','Manual','Incremental')),
    operation text NOT NULL
        CHECK (operation IN ('QueueSnapshot','SnapshotStarted','SnapshotCompleted',
                             'SnapshotFailed','AtomicUpsert','AtomicDelete')),
    status text NOT NULL
        CHECK (status IN ('Pending','Running','Succeeded','Failed')),
    snapshot_started_at timestamptz,
    snapshot_completed_at timestamptz,
    observed_count bigint CHECK (observed_count IS NULL OR observed_count >= 0),
    source_cursor text,
    source_updated_at timestamptz,
    error_code text,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, event_id)
);

CREATE INDEX IF NOT EXISTS entity_refresh_events_connection_idx
    ON entitysync.entity_refresh_events (tenant_id, connection_id, entity_type, received_at DESC);
CREATE INDEX IF NOT EXISTS entity_refresh_events_completion_idx
    ON entitysync.entity_refresh_events (tenant_id, snapshot_completed_at DESC)
    WHERE operation IN ('SnapshotCompleted','SnapshotFailed');

-- Atomic event receipt: idempotent per (tenant, event_id).
CREATE TABLE IF NOT EXISTS entitysync.entity_event_receipts (
    tenant_id text NOT NULL,
    event_id uuid NOT NULL,
    connection_id text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    operation text NOT NULL
        CHECK (operation IN ('Upsert','Delete')),
    payload_sha256 char(64) NOT NULL,
    source_cursor text,
    source_updated_at timestamptz,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, event_id),
    CHECK (payload_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS entity_event_receipts_entity_idx
    ON entitysync.entity_event_receipts (
        tenant_id, connection_id, entity_type, entity_id, received_at DESC);

-- Connection readable entity-type capability cache: scheduler reads from this,
-- never directly from the live adapter.
CREATE TABLE IF NOT EXISTS entitysync.connection_refresh_capabilities (
    tenant_id text NOT NULL,
    connection_id text NOT NULL,
    vendor text NOT NULL,
    entity_type text NOT NULL,
    supports_refresh boolean NOT NULL,
    last_discovered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, connection_id, entity_type)
);

CREATE INDEX IF NOT EXISTS connection_refresh_capabilities_refresh_idx
    ON entitysync.connection_refresh_capabilities (tenant_id, supports_refresh, entity_type);

-- Authoritative snapshot boundary tombstoning guard.
-- Atomic-event application may insert/update records whose last_observed_at >=
-- any active snapshot_started_at; the snapshot replace must never erase them.
CREATE OR REPLACE FUNCTION entitysync.enforce_entity_refresh_state_lease()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    IF NEW.lease_owner IS NOT NULL
       AND NEW.lease_expires_at IS NOT NULL
       AND NEW.lease_expires_at <= clock_timestamp() THEN
        NEW.lease_owner := NULL;
        NEW.lease_expires_at := NULL;
        NEW.status := CASE WHEN NEW.status = 'Running' THEN 'Pending' ELSE NEW.status END;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS entity_refresh_state_lease_expiry
    ON entitysync.entity_refresh_state;
CREATE TRIGGER entity_refresh_state_lease_expiry
    BEFORE UPDATE ON entitysync.entity_refresh_state
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_entity_refresh_state_lease();

ALTER TABLE entitysync.entity_records
    ADD COLUMN IF NOT EXISTS source_cursor text;
ALTER TABLE entitysync.entity_records
    ADD COLUMN IF NOT EXISTS source_updated_at timestamptz;

CREATE INDEX IF NOT EXISTS entity_records_source_updated_idx
    ON entitysync.entity_records (tenant_id, connection_id, entity_type, source_updated_at DESC);
