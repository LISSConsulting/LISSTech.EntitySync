CREATE TABLE IF NOT EXISTS entitysync.sync_plans (
    tenant_id text NOT NULL,
    plan_id uuid NOT NULL,
    policy_id uuid NOT NULL,
    policy_version integer NOT NULL CHECK (policy_version > 0),
    route_scope text NOT NULL,
    plan_digest_sha256 char(64) NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, plan_id),
    UNIQUE (tenant_id, plan_digest_sha256),
    UNIQUE (tenant_id, plan_id, plan_digest_sha256),
    CONSTRAINT sync_plans_status_check
        CHECK (status IN ('Draft','Approved','Consumed','Expired')),
    CONSTRAINT sync_plans_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plans_policy_fkey
        FOREIGN KEY (tenant_id, policy_id, policy_version)
        REFERENCES entitysync.sync_policies (tenant_id, policy_id, version)
);

CREATE INDEX IF NOT EXISTS sync_plans_tenant_status_expires_idx
    ON entitysync.sync_plans (tenant_id, status, expires_at);

CREATE TABLE IF NOT EXISTS entitysync.sync_plan_items (
    tenant_id text NOT NULL,
    plan_id uuid NOT NULL,
    item_id uuid NOT NULL,
    item_ordinal integer NOT NULL CHECK (item_ordinal >= 0),
    source_vendor text NOT NULL,
    source_connection_id text NOT NULL,
    source_entity_type text NOT NULL,
    source_entity_key text NOT NULL,
    source_entity_id text NOT NULL,
    target_vendor text NOT NULL,
    target_connection_id text NOT NULL,
    target_entity_type text NOT NULL,
    target_entity_id text,
    action text NOT NULL,
    redacted_before jsonb NOT NULL,
    redacted_desired jsonb NOT NULL,
    before_payload_sha256 char(64),
    desired_payload_sha256 char(64) NOT NULL,
    PRIMARY KEY (tenant_id, plan_id, item_id),
    UNIQUE (tenant_id, plan_id, item_ordinal),
    CONSTRAINT sync_plan_items_plan_fkey
        FOREIGN KEY (tenant_id, plan_id)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id),
    CONSTRAINT sync_plan_items_before_payload_sha256_check
        CHECK (before_payload_sha256 IS NULL OR before_payload_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plan_items_desired_payload_sha256_check
        CHECK (desired_payload_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_plan_items_tenant_source_idx
    ON entitysync.sync_plan_items (
        tenant_id, source_vendor, source_connection_id, source_entity_type, source_entity_key);

CREATE TABLE IF NOT EXISTS entitysync.sync_plan_inspections (
    tenant_id text NOT NULL,
    inspection_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    plan_digest_sha256 char(64) NOT NULL,
    range_start integer NOT NULL,
    range_end integer NOT NULL,
    inspected_at timestamptz NOT NULL,
    inspected_by text NOT NULL,
    PRIMARY KEY (tenant_id, inspection_id),
    CONSTRAINT sync_plan_inspections_plan_digest_fkey
        FOREIGN KEY (tenant_id, plan_id, plan_digest_sha256)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id, plan_digest_sha256),
    CONSTRAINT sync_plan_inspections_range_check
        CHECK (range_start >= 0 AND range_end >= range_start),
    CONSTRAINT sync_plan_inspections_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_plan_inspections_tenant_plan_range_idx
    ON entitysync.sync_plan_inspections (tenant_id, plan_id, range_start, range_end);

CREATE TABLE IF NOT EXISTS entitysync.sync_approvals (
    tenant_id text NOT NULL,
    approval_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    plan_digest_sha256 char(64) NOT NULL,
    approved_at timestamptz NOT NULL,
    approved_by text NOT NULL,
    expires_at timestamptz,
    PRIMARY KEY (tenant_id, approval_id),
    UNIQUE (tenant_id, plan_digest_sha256),
    UNIQUE (tenant_id, approval_id, plan_id),
    CONSTRAINT sync_approvals_plan_digest_fkey
        FOREIGN KEY (tenant_id, plan_id, plan_digest_sha256)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id, plan_digest_sha256),
    CONSTRAINT sync_approvals_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_approvals_tenant_plan_idx
    ON entitysync.sync_approvals (tenant_id, plan_id);

CREATE TABLE IF NOT EXISTS entitysync.api_idempotency_records (
    tenant_id text NOT NULL,
    idempotency_key text NOT NULL,
    request_sha256 char(64) NOT NULL,
    response_status_code integer,
    response_body jsonb,
    created_at timestamptz NOT NULL,
    completed_at timestamptz,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key),
    CONSTRAINT api_idempotency_records_request_sha256_check
        CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT api_idempotency_records_response_complete_check
        CHECK ((response_status_code IS NULL) = (response_body IS NULL))
);

CREATE INDEX IF NOT EXISTS api_idempotency_records_expiry_idx
    ON entitysync.api_idempotency_records (tenant_id, expires_at);

CREATE TABLE IF NOT EXISTS entitysync.sync_operations (
    tenant_id text NOT NULL,
    operation_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    approval_id uuid,
    route_scope text NOT NULL,
    mode text NOT NULL,
    status text NOT NULL,
    idempotency_key text NOT NULL,
    lease_owner text,
    lease_expires_at timestamptz,
    attempt integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    queued_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    CONSTRAINT sync_operation_pkey PRIMARY KEY (tenant_id, operation_id),
    UNIQUE (tenant_id, idempotency_key),
    CONSTRAINT sync_operations_plan_fkey
        FOREIGN KEY (tenant_id, plan_id)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id),
    CONSTRAINT sync_operations_approval_plan_fkey
        FOREIGN KEY (tenant_id, approval_id, plan_id)
        REFERENCES entitysync.sync_approvals (tenant_id, approval_id, plan_id),
    CONSTRAINT sync_operations_mode_check CHECK (mode IN ('DryRun','Apply')),
    CONSTRAINT sync_operations_status_check
        CHECK (status IN ('Queued','Leased','Running','Succeeded','Partial','Failed','Cancelled')),
    CONSTRAINT sync_operations_attempt_check CHECK (attempt >= 0),
    CONSTRAINT sync_operations_lease_check
        CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL))
);

CREATE INDEX IF NOT EXISTS sync_operations_tenant_status_lease_idx
    ON entitysync.sync_operations (tenant_id, status, lease_expires_at);
CREATE INDEX IF NOT EXISTS sync_operations_tenant_plan_idx
    ON entitysync.sync_operations (tenant_id, plan_id);

CREATE TABLE IF NOT EXISTS entitysync.sync_operation_items (
    tenant_id text NOT NULL,
    operation_id uuid NOT NULL,
    item_id uuid NOT NULL,
    source_vendor text NOT NULL,
    source_connection_id text NOT NULL,
    source_entity_type text NOT NULL,
    source_entity_key text NOT NULL,
    source_entity_id text NOT NULL,
    target_vendor text NOT NULL,
    target_connection_id text NOT NULL,
    target_entity_type text NOT NULL,
    target_entity_id text,
    action text NOT NULL,
    redacted_before jsonb NOT NULL,
    redacted_desired jsonb NOT NULL,
    encrypted_before_ciphertext text,
    encrypted_after_ciphertext text,
    before_payload_sha256 char(64),
    desired_payload_sha256 char(64) NOT NULL,
    after_payload_sha256 char(64),
    vendor_request_id text,
    outcome text NOT NULL,
    error_code text,
    error_message text,
    started_at timestamptz,
    completed_at timestamptz,
    PRIMARY KEY (tenant_id, operation_id, item_id),
    CONSTRAINT sync_operation_items_operation_fkey
        FOREIGN KEY (tenant_id, operation_id)
        REFERENCES entitysync.sync_operations (tenant_id, operation_id),
    CONSTRAINT sync_operation_items_outcome_check
        CHECK (outcome IN ('Pending','Succeeded','Failed','Skipped','Unknown')),
    CONSTRAINT sync_operation_items_before_payload_sha256_check
        CHECK (before_payload_sha256 IS NULL OR before_payload_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_operation_items_desired_payload_sha256_check
        CHECK (desired_payload_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_operation_items_after_payload_sha256_check
        CHECK (after_payload_sha256 IS NULL OR after_payload_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_operation_items_tenant_outcome_idx
    ON entitysync.sync_operation_items (tenant_id, operation_id, outcome);
CREATE INDEX IF NOT EXISTS sync_operation_items_vendor_request_idx
    ON entitysync.sync_operation_items (tenant_id, vendor_request_id)
    WHERE vendor_request_id IS NOT NULL;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_plan_immutability()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rows in %.% cannot be deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
        OR NEW.policy_id IS DISTINCT FROM OLD.policy_id
        OR NEW.policy_version IS DISTINCT FROM OLD.policy_version
        OR NEW.route_scope IS DISTINCT FROM OLD.route_scope
        OR NEW.plan_digest_sha256 IS DISTINCT FROM OLD.plan_digest_sha256
        OR NEW.created_at IS DISTINCT FROM OLD.created_at
        OR NEW.created_by IS DISTINCT FROM OLD.created_by
        OR NEW.expires_at IS DISTINCT FROM OLD.expires_at THEN
        RAISE EXCEPTION 'Plan content in %.% is immutable', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_operation_item_immutability()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rows in %.% cannot be deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.operation_id IS DISTINCT FROM OLD.operation_id
        OR NEW.item_id IS DISTINCT FROM OLD.item_id
        OR NEW.source_vendor IS DISTINCT FROM OLD.source_vendor
        OR NEW.source_connection_id IS DISTINCT FROM OLD.source_connection_id
        OR NEW.source_entity_type IS DISTINCT FROM OLD.source_entity_type
        OR NEW.source_entity_key IS DISTINCT FROM OLD.source_entity_key
        OR NEW.source_entity_id IS DISTINCT FROM OLD.source_entity_id
        OR NEW.target_vendor IS DISTINCT FROM OLD.target_vendor
        OR NEW.target_connection_id IS DISTINCT FROM OLD.target_connection_id
        OR NEW.target_entity_type IS DISTINCT FROM OLD.target_entity_type
        OR NEW.target_entity_id IS DISTINCT FROM OLD.target_entity_id
        OR NEW.action IS DISTINCT FROM OLD.action
        OR NEW.redacted_before IS DISTINCT FROM OLD.redacted_before
        OR NEW.redacted_desired IS DISTINCT FROM OLD.redacted_desired
        OR NEW.encrypted_before_ciphertext IS DISTINCT FROM OLD.encrypted_before_ciphertext
        OR NEW.before_payload_sha256 IS DISTINCT FROM OLD.before_payload_sha256
        OR NEW.desired_payload_sha256 IS DISTINCT FROM OLD.desired_payload_sha256 THEN
        RAISE EXCEPTION 'Operation item identity and planned input in %.% are immutable',
            TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_plans_immutable ON entitysync.sync_plans;
CREATE TRIGGER sync_plans_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_plans
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_plan_immutability();

DROP TRIGGER IF EXISTS sync_plan_items_immutable ON entitysync.sync_plan_items;
CREATE TRIGGER sync_plan_items_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_plan_items
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();

DROP TRIGGER IF EXISTS sync_plan_inspections_immutable ON entitysync.sync_plan_inspections;
CREATE TRIGGER sync_plan_inspections_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_plan_inspections
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();

DROP TRIGGER IF EXISTS sync_approvals_immutable ON entitysync.sync_approvals;
CREATE TRIGGER sync_approvals_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_approvals
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();

DROP TRIGGER IF EXISTS sync_operation_items_immutable ON entitysync.sync_operation_items;
CREATE TRIGGER sync_operation_items_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_operation_items
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_operation_item_immutability();
