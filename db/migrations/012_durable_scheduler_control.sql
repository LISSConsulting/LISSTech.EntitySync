ALTER TABLE entitysync.sync_schedules
    ADD COLUMN IF NOT EXISTS runtime_revision bigint NOT NULL DEFAULT 0;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_schedule_versions()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Schedule versions cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.schedule_id IS DISTINCT FROM OLD.schedule_id
       OR NEW.version IS DISTINCT FROM OLD.version
       OR NEW.name IS DISTINCT FROM OLD.name
       OR NEW.policy_id IS DISTINCT FROM OLD.policy_id
       OR NEW.policy_version IS DISTINCT FROM OLD.policy_version
       OR NEW.cron_expression IS DISTINCT FROM OLD.cron_expression
       OR NEW.time_zone IS DISTINCT FROM OLD.time_zone
       OR NEW.enabled IS DISTINCT FROM OLD.enabled
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'Schedule version definitions are immutable' USING ERRCODE = '55000';
    END IF;
    IF NEW.runtime_revision <> OLD.runtime_revision + 1 THEN
        RAISE EXCEPTION 'Schedule runtime updates require an exact revision fence' USING ERRCODE = '40001';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_schedules_immutable ON entitysync.sync_schedules;
CREATE TRIGGER sync_schedules_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_schedules
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_schedule_versions();

ALTER TABLE entitysync.canonical_change_events
    ADD COLUMN IF NOT EXISTS receipt_id uuid,
    ADD COLUMN IF NOT EXISTS om_event_id text,
    ADD COLUMN IF NOT EXISTS payload_sha256 char(64);

UPDATE entitysync.canonical_change_events
SET receipt_id = COALESCE(receipt_id, event_id),
    om_event_id = COALESCE(om_event_id, event_id::text),
    payload_sha256 = COALESCE(payload_sha256, md5(event_id::text) || md5(event_id::text));

ALTER TABLE entitysync.canonical_change_events
    ALTER COLUMN receipt_id SET NOT NULL,
    ALTER COLUMN om_event_id SET NOT NULL,
    ALTER COLUMN payload_sha256 SET NOT NULL;

ALTER TABLE entitysync.canonical_change_events
    DROP CONSTRAINT IF EXISTS canonical_change_events_payload_sha256_check;
ALTER TABLE entitysync.canonical_change_events
    ADD CONSTRAINT canonical_change_events_payload_sha256_check
        CHECK (payload_sha256 ~ '^[0-9a-f]{64}$');

CREATE UNIQUE INDEX IF NOT EXISTS canonical_change_events_outbox_uidx
    ON entitysync.canonical_change_events (tenant_id, om_event_id);
CREATE UNIQUE INDEX IF NOT EXISTS canonical_change_events_receipt_uidx
    ON entitysync.canonical_change_events (tenant_id, receipt_id);

CREATE TABLE IF NOT EXISTS entitysync.sync_control_work (
    tenant_id text NOT NULL,
    work_id uuid NOT NULL,
    work_kind text NOT NULL CHECK (work_kind IN ('Schedule','CanonicalChange')),
    state text NOT NULL CHECK (state IN ('Queued','Leased','Planning','Held','Completed')),
    checkpoint text NOT NULL DEFAULT 'Pending'
        CHECK (checkpoint IN ('Pending','Planned','Approved','OperationQueued')),
    policy_id uuid NOT NULL,
    policy_version integer NOT NULL CHECK (policy_version > 0),
    route_scope text NOT NULL,
    schedule_id uuid,
    schedule_version integer,
    scheduled_for timestamptz,
    canonical_event_id uuid,
    canonical_entity_type text,
    canonical_entity_id uuid,
    canonical_version bigint,
    changed_fields jsonb,
    payload_sha256 char(64),
    plan_digest_sha256 char(64),
    not_before timestamptz NOT NULL DEFAULT '-infinity',
    lease_owner text,
    lease_expires_at timestamptz,
    attempt integer NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    plan_id uuid,
    approval_id uuid,
    operation_id uuid,
    hold_reason text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, work_id),
    FOREIGN KEY (tenant_id, policy_id, policy_version)
        REFERENCES entitysync.sync_policies (tenant_id, policy_id, version),
    FOREIGN KEY (tenant_id, canonical_event_id)
        REFERENCES entitysync.canonical_change_events (tenant_id, event_id),
    CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CHECK ((work_kind = 'Schedule') =
        (schedule_id IS NOT NULL AND schedule_version IS NOT NULL AND scheduled_for IS NOT NULL)),
    CHECK ((work_kind = 'CanonicalChange') =
        (canonical_event_id IS NOT NULL AND canonical_entity_type IS NOT NULL
         AND canonical_entity_id IS NOT NULL AND canonical_version IS NOT NULL
         AND changed_fields IS NOT NULL AND payload_sha256 IS NOT NULL)),
    CHECK (payload_sha256 IS NULL OR payload_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (plan_digest_sha256 IS NULL OR plan_digest_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK ((plan_id IS NULL) = (plan_digest_sha256 IS NULL)),
    CHECK (checkpoint = 'Pending' OR plan_id IS NOT NULL),
    CHECK (checkpoint NOT IN ('Approved','OperationQueued') OR approval_id IS NOT NULL),
    CHECK (checkpoint <> 'OperationQueued' OR operation_id IS NOT NULL),
    CHECK ((state = 'Held') = (hold_reason IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS sync_control_work_schedule_uidx
    ON entitysync.sync_control_work (
        tenant_id, schedule_id, schedule_version, scheduled_for)
    WHERE work_kind = 'Schedule';
CREATE UNIQUE INDEX IF NOT EXISTS sync_control_work_canonical_policy_uidx
    ON entitysync.sync_control_work (tenant_id, canonical_event_id, policy_id, policy_version)
    WHERE work_kind = 'CanonicalChange';
CREATE INDEX IF NOT EXISTS sync_control_work_lease_idx
    ON entitysync.sync_control_work (tenant_id, state, not_before, lease_expires_at, created_at);

CREATE TABLE IF NOT EXISTS entitysync.sync_route_leases (
    tenant_id text NOT NULL,
    route_scope text NOT NULL,
    lease_owner text NOT NULL,
    lease_token uuid NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    attempt bigint NOT NULL CHECK (attempt > 0),
    PRIMARY KEY (tenant_id, route_scope)
);

ALTER TABLE entitysync.audit_events
    ADD COLUMN IF NOT EXISTS values_redacted_at timestamptz;

CREATE OR REPLACE FUNCTION entitysync.enforce_audit_event_redaction_only()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE'
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.audit_event_id IS DISTINCT FROM OLD.audit_event_id
       OR NEW.occurred_at IS DISTINCT FROM OLD.occurred_at
       OR NEW.event_type IS DISTINCT FROM OLD.event_type
       OR NEW.actor_id IS DISTINCT FROM OLD.actor_id
       OR NEW.operation_id IS DISTINCT FROM OLD.operation_id
       OR NEW.run_id IS DISTINCT FROM OLD.run_id
       OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
       OR NEW.item_id IS DISTINCT FROM OLD.item_id
       OR NEW.correlation_id IS DISTINCT FROM OLD.correlation_id
       OR NEW.redacted_values IS DISTINCT FROM OLD.redacted_values
       OR NEW.redacted_values_sha256 IS DISTINCT FROM OLD.redacted_values_sha256
       OR NEW.full_values_sha256 IS DISTINCT FROM OLD.full_values_sha256
       OR NEW.full_values_expires_at IS DISTINCT FROM OLD.full_values_expires_at
       OR OLD.values_redacted_at IS NOT NULL
       OR NEW.values_redacted_at IS NULL THEN
        RAISE EXCEPTION 'Audit identity and hashes are immutable; only redaction may advance'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS audit_events_immutable ON entitysync.audit_events;
CREATE TRIGGER audit_events_immutable
    BEFORE UPDATE OR DELETE ON entitysync.audit_events
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_audit_event_redaction_only();
ALTER TABLE entitysync.audit_event_full_values
    ALTER COLUMN full_values_ciphertext DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS values_redacted_at timestamptz;
ALTER TABLE entitysync.audit_event_full_values
    DROP CONSTRAINT IF EXISTS audit_event_full_values_redaction_check;
ALTER TABLE entitysync.audit_event_full_values
    ADD CONSTRAINT audit_event_full_values_redaction_check
        CHECK ((full_values_ciphertext IS NULL) = (values_redacted_at IS NOT NULL));

ALTER TABLE entitysync.sync_operation_item_snapshots
    ADD COLUMN IF NOT EXISTS values_redacted_at timestamptz;
ALTER TABLE entitysync.sync_operation_item_snapshots
    DROP CONSTRAINT IF EXISTS sync_operation_item_snapshots_values_check;
ALTER TABLE entitysync.sync_operation_item_snapshots
    ADD CONSTRAINT sync_operation_item_snapshots_values_check
        CHECK ((encrypted_before_ciphertext IS NOT NULL OR encrypted_after_ciphertext IS NOT NULL)
               = (values_redacted_at IS NULL));

CREATE OR REPLACE FUNCTION entitysync.enforce_retained_ciphertext_scrub()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Retention rows preserve metadata and cannot be deleted'
            USING ERRCODE = '55000';
    END IF;
    IF OLD.expires_at > clock_timestamp()
       OR OLD.values_redacted_at IS NOT NULL
       OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.values_redacted_at IS NULL THEN
        RAISE EXCEPTION 'Ciphertext can only be scrubbed once after database expiry'
            USING ERRCODE = '55000';
    END IF;
    IF TG_TABLE_NAME = 'audit_event_full_values' THEN
        IF NEW.audit_event_id IS DISTINCT FROM OLD.audit_event_id
           OR NEW.full_values_ciphertext IS NOT NULL THEN
            RAISE EXCEPTION 'Audit retention scrub cannot alter identity or metadata'
                USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'sync_operation_item_snapshots' THEN
        IF NEW.operation_id IS DISTINCT FROM OLD.operation_id
           OR NEW.item_id IS DISTINCT FROM OLD.item_id
           OR NEW.encrypted_before_ciphertext IS NOT NULL
           OR NEW.encrypted_after_ciphertext IS NOT NULL THEN
            RAISE EXCEPTION 'Operation snapshot scrub cannot alter identity or metadata'
                USING ERRCODE = '55000';
        END IF;
    ELSE
        RAISE EXCEPTION 'Unsupported retention table'
            USING ERRCODE = '55000';
    END IF;
    NEW.values_redacted_at := clock_timestamp();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS audit_event_full_values_retention ON entitysync.audit_event_full_values;
CREATE TRIGGER audit_event_full_values_retention
    BEFORE UPDATE OR DELETE ON entitysync.audit_event_full_values
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_retained_ciphertext_scrub();

DROP TRIGGER IF EXISTS sync_operation_item_snapshots_retention
    ON entitysync.sync_operation_item_snapshots;
CREATE TRIGGER sync_operation_item_snapshots_retention
    BEFORE UPDATE OR DELETE ON entitysync.sync_operation_item_snapshots
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_retained_ciphertext_scrub();
