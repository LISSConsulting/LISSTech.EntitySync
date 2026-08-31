CREATE TABLE IF NOT EXISTS entitysync.sync_schedules (
    tenant_id text NOT NULL,
    schedule_id uuid NOT NULL,
    version integer NOT NULL,
    name text NOT NULL,
    policy_id uuid NOT NULL,
    policy_version integer NOT NULL,
    cron_expression text NOT NULL,
    time_zone text NOT NULL,
    enabled boolean NOT NULL,
    next_run_at timestamptz,
    last_run_at timestamptz,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    PRIMARY KEY (tenant_id, schedule_id, version),
    CONSTRAINT sync_schedules_version_check CHECK (version > 0),
    CONSTRAINT sync_schedules_policy_version_check CHECK (policy_version > 0),
    CONSTRAINT sync_schedules_policy_fkey
        FOREIGN KEY (tenant_id, policy_id, policy_version)
        REFERENCES entitysync.sync_policies (tenant_id, policy_id, version)
);

CREATE INDEX IF NOT EXISTS sync_schedules_tenant_due_idx
    ON entitysync.sync_schedules (tenant_id, next_run_at)
    WHERE enabled;

CREATE TABLE IF NOT EXISTS entitysync.canonical_change_events (
    tenant_id text NOT NULL,
    event_id uuid NOT NULL,
    canonical_entity_type text NOT NULL,
    canonical_entity_id text NOT NULL,
    canonical_version bigint NOT NULL,
    changed_fields jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    status text NOT NULL CHECK (status IN ('Pending','Planned','Ignored','Failed')),
    PRIMARY KEY (tenant_id, event_id),
    CONSTRAINT canonical_change_events_version_check CHECK (canonical_version > 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS canonical_change_events_entity_version_uidx
    ON entitysync.canonical_change_events (
        tenant_id, canonical_entity_type, canonical_entity_id, canonical_version);
CREATE INDEX IF NOT EXISTS canonical_change_events_tenant_status_received_idx
    ON entitysync.canonical_change_events (tenant_id, status, received_at);

CREATE TABLE IF NOT EXISTS entitysync.audit_events (
    tenant_id text NOT NULL,
    audit_event_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    event_type text NOT NULL,
    actor_id text NOT NULL,
    operation_id uuid,
    run_id uuid,
    plan_id uuid,
    item_id uuid,
    correlation_id text NOT NULL,
    redacted_values jsonb NOT NULL,
    redacted_values_sha256 char(64) NOT NULL,
    full_values_ciphertext text,
    full_values_sha256 char(64),
    full_values_expires_at timestamptz,
    PRIMARY KEY (tenant_id, audit_event_id),
    CONSTRAINT audit_events_redacted_values_sha256_check
        CHECK (redacted_values_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT audit_events_full_values_sha256_check
        CHECK (full_values_sha256 IS NULL OR full_values_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT audit_events_full_values_complete_check
        CHECK (
            (full_values_ciphertext IS NULL AND full_values_sha256 IS NULL AND full_values_expires_at IS NULL)
            OR
            (full_values_ciphertext IS NOT NULL AND full_values_sha256 IS NOT NULL AND full_values_expires_at IS NOT NULL)
        ),
    CONSTRAINT audit_events_full_values_retention_check
        CHECK (full_values_expires_at IS NULL OR full_values_expires_at <= occurred_at + interval '365 days')
);

CREATE INDEX IF NOT EXISTS audit_events_tenant_occurred_idx
    ON entitysync.audit_events (tenant_id, occurred_at);
CREATE INDEX IF NOT EXISTS audit_events_operation_idx
    ON entitysync.audit_events (tenant_id, operation_id)
    WHERE operation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS audit_events_run_idx
    ON entitysync.audit_events (tenant_id, run_id)
    WHERE run_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS audit_events_plan_item_idx
    ON entitysync.audit_events (tenant_id, plan_id, item_id)
    WHERE plan_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS audit_events_correlation_idx
    ON entitysync.audit_events (tenant_id, correlation_id);
CREATE INDEX IF NOT EXISTS audit_events_full_values_expiry_idx
    ON entitysync.audit_events (tenant_id, full_values_expires_at)
    WHERE full_values_expires_at IS NOT NULL;

DROP TRIGGER IF EXISTS audit_events_immutable ON entitysync.audit_events;
CREATE TRIGGER audit_events_immutable
    BEFORE UPDATE OR DELETE ON entitysync.audit_events
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();
