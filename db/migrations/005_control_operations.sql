CREATE TABLE IF NOT EXISTS entitysync.sync_plans (
    tenant_id text NOT NULL,
    plan_id uuid NOT NULL,
    policy_id uuid NOT NULL,
    policy_version integer NOT NULL CHECK (policy_version > 0),
    route_scope text NOT NULL,
    source_connection_id text NOT NULL,
    target_connection_id text NOT NULL,
    source_connection_generation bigint NOT NULL CHECK (source_connection_generation > 0),
    target_connection_generation bigint NOT NULL CHECK (target_connection_generation > 0),
    source_search text,
    source_count integer,
    source_entity_id text,
    plan_digest_sha256 char(64) NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, plan_id),
    UNIQUE (tenant_id, plan_digest_sha256),
    UNIQUE (
        tenant_id, plan_id, plan_digest_sha256,
        source_connection_generation, target_connection_generation),
    UNIQUE (tenant_id, plan_id, source_connection_generation, target_connection_generation),
    CONSTRAINT sync_plans_status_check
        CHECK (status IN ('Draft','Approved','Consumed','Expired')),
    CONSTRAINT sync_plans_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plans_source_count_check
        CHECK (source_count IS NULL OR source_count > 0),
    CONSTRAINT sync_plans_source_count_search_check
        CHECK (source_count IS NULL OR source_search IS NOT NULL),
    CONSTRAINT sync_plans_connection_ids_check
        CHECK (btrim(source_connection_id) <> '' AND btrim(target_connection_id) <> ''),
    CONSTRAINT sync_plans_source_search_check
        CHECK (source_search IS NULL OR btrim(source_search) <> ''),
    CONSTRAINT sync_plans_source_entity_id_check
        CHECK (source_entity_id IS NULL OR btrim(source_entity_id) <> ''),
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
    match_score integer NOT NULL,
    match_type text NOT NULL,
    match_reasons jsonb NOT NULL,
    field_diffs jsonb NOT NULL,
    redacted_before jsonb NOT NULL,
    redacted_desired jsonb NOT NULL,
    before_payload_sha256 char(64),
    desired_payload_sha256 char(64) NOT NULL,
    PRIMARY KEY (tenant_id, plan_id, item_id),
    UNIQUE (tenant_id, plan_id, item_ordinal),
    CONSTRAINT sync_plan_items_plan_fkey
        FOREIGN KEY (tenant_id, plan_id)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id),
    CONSTRAINT sync_plan_items_match_score_check
        CHECK (match_score BETWEEN 0 AND 100),
    CONSTRAINT sync_plan_items_match_type_check
        CHECK (btrim(match_type) <> ''),
    CONSTRAINT sync_plan_items_match_reasons_check
        CHECK (jsonb_typeof(match_reasons) = 'array'),
    CONSTRAINT sync_plan_items_field_diffs_check
        CHECK (jsonb_typeof(field_diffs) = 'array'),
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
    source_connection_generation bigint NOT NULL CHECK (source_connection_generation > 0),
    target_connection_generation bigint NOT NULL CHECK (target_connection_generation > 0),
    status text NOT NULL,
    inspected_at timestamptz NOT NULL,
    inspected_by text NOT NULL,
    completed_at timestamptz,
    PRIMARY KEY (tenant_id, inspection_id),
    UNIQUE (
        tenant_id, inspection_id, plan_id, plan_digest_sha256,
        source_connection_generation, target_connection_generation),
    CONSTRAINT sync_plan_inspections_plan_digest_fkey
        FOREIGN KEY (
            tenant_id, plan_id, plan_digest_sha256,
            source_connection_generation, target_connection_generation)
        REFERENCES entitysync.sync_plans (
            tenant_id, plan_id, plan_digest_sha256,
            source_connection_generation, target_connection_generation),
    CONSTRAINT sync_plan_inspections_status_check
        CHECK (status IN ('Open','Completed')),
    CONSTRAINT sync_plan_inspections_completion_check
        CHECK ((status = 'Open' AND completed_at IS NULL)
            OR (status = 'Completed' AND completed_at IS NOT NULL)),
    CONSTRAINT sync_plan_inspections_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_plan_inspections_tenant_plan_idx
    ON entitysync.sync_plan_inspections (tenant_id, plan_id, status);

CREATE TABLE IF NOT EXISTS entitysync.sync_plan_inspection_ranges (
    tenant_id text NOT NULL,
    inspection_id uuid NOT NULL,
    range_id uuid NOT NULL,
    range_start integer NOT NULL,
    range_end integer NOT NULL,
    inspected_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, inspection_id, range_id),
    CONSTRAINT sync_plan_inspection_ranges_session_fkey
        FOREIGN KEY (tenant_id, inspection_id)
        REFERENCES entitysync.sync_plan_inspections (tenant_id, inspection_id),
    CONSTRAINT sync_plan_inspection_ranges_bounds_check
        CHECK (range_start >= 0 AND range_end >= range_start)
);

CREATE INDEX IF NOT EXISTS sync_plan_inspection_ranges_coverage_idx
    ON entitysync.sync_plan_inspection_ranges (
        tenant_id, inspection_id, range_start, range_end);

CREATE TABLE IF NOT EXISTS entitysync.sync_approvals (
    tenant_id text NOT NULL,
    approval_id uuid NOT NULL,
    inspection_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    plan_digest_sha256 char(64) NOT NULL,
    source_connection_generation bigint NOT NULL CHECK (source_connection_generation > 0),
    target_connection_generation bigint NOT NULL CHECK (target_connection_generation > 0),
    approved_at timestamptz NOT NULL,
    approved_by text NOT NULL,
    expires_at timestamptz,
    PRIMARY KEY (tenant_id, approval_id),
    UNIQUE (tenant_id, plan_digest_sha256),
    UNIQUE (
        tenant_id, approval_id, plan_id,
        source_connection_generation, target_connection_generation),
    CONSTRAINT sync_approvals_inspection_fkey
        FOREIGN KEY (
            tenant_id, inspection_id, plan_id, plan_digest_sha256,
            source_connection_generation, target_connection_generation)
        REFERENCES entitysync.sync_plan_inspections (
            tenant_id, inspection_id, plan_id, plan_digest_sha256,
            source_connection_generation, target_connection_generation),
    CONSTRAINT sync_approvals_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_approvals_tenant_plan_idx
    ON entitysync.sync_approvals (tenant_id, plan_id);

CREATE OR REPLACE FUNCTION entitysync.enforce_complete_inspection_coverage()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM entitysync.sync_plan_inspections
         WHERE tenant_id = NEW.tenant_id
           AND inspection_id = NEW.inspection_id
           AND plan_id = NEW.plan_id
           AND plan_digest_sha256 = NEW.plan_digest_sha256
           AND source_connection_generation = NEW.source_connection_generation
           AND target_connection_generation = NEW.target_connection_generation
           AND status = 'Completed'
           AND completed_at IS NOT NULL) THEN
        RAISE EXCEPTION 'Approval requires a completed inspection session'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_inspection_completion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    item_count integer;
    first_ordinal integer;
    last_ordinal integer;
    final_covered_ordinal integer;
    invalid_ranges integer;
BEGIN
    IF TG_OP = 'DELETE'
        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.inspection_id IS DISTINCT FROM OLD.inspection_id
        OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
        OR NEW.plan_digest_sha256 IS DISTINCT FROM OLD.plan_digest_sha256
        OR NEW.source_connection_generation IS DISTINCT FROM OLD.source_connection_generation
        OR NEW.target_connection_generation IS DISTINCT FROM OLD.target_connection_generation
        OR NEW.inspected_at IS DISTINCT FROM OLD.inspected_at
        OR NEW.inspected_by IS DISTINCT FROM OLD.inspected_by
        OR OLD.status <> 'Open'
        OR NEW.status <> 'Completed'
        OR OLD.completed_at IS NOT NULL
        OR NEW.completed_at IS NULL THEN
        RAISE EXCEPTION 'Inspection sessions allow only one Open-to-Completed transition'
            USING ERRCODE = '55000';
    END IF;

    SELECT count(*)::integer, min(item_ordinal), max(item_ordinal)
      INTO item_count, first_ordinal, last_ordinal
      FROM entitysync.sync_plan_items
     WHERE tenant_id = NEW.tenant_id AND plan_id = NEW.plan_id;

    WITH ordered_ranges AS (
        SELECT range_start,
               range_end,
               row_number() OVER (ORDER BY range_start, range_end) AS position,
               lag(range_end) OVER (ORDER BY range_start, range_end) AS previous_end
          FROM entitysync.sync_plan_inspection_ranges
         WHERE tenant_id = NEW.tenant_id AND inspection_id = NEW.inspection_id
    )
    SELECT count(*) FILTER (
               WHERE (position = 1 AND range_start <> 0)
                  OR (position > 1 AND range_start <> previous_end + 1)
                  OR range_end >= item_count),
           max(range_end)
      INTO invalid_ranges, final_covered_ordinal
      FROM ordered_ranges;

    IF item_count = 0
        OR first_ordinal <> 0
        OR last_ordinal <> item_count - 1
        OR invalid_ranges <> 0
        OR final_covered_ordinal IS NULL
        OR final_covered_ordinal <> item_count - 1 THEN
        RAISE EXCEPTION 'Inspection ranges must exactly cover every plan ordinal without gaps or overlaps'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_open_inspection_range()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    session_status text;
BEGIN
    SELECT status
      INTO session_status
      FROM entitysync.sync_plan_inspections
     WHERE tenant_id = NEW.tenant_id AND inspection_id = NEW.inspection_id
     FOR UPDATE;
    IF session_status IS NULL THEN
        RETURN NEW;
    END IF;

    IF session_status <> 'Open' THEN
        RAISE EXCEPTION 'Inspection ranges require an open inspection session'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_approvals_complete_inspection ON entitysync.sync_approvals;
CREATE TRIGGER sync_approvals_complete_inspection
    BEFORE INSERT ON entitysync.sync_approvals
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_complete_inspection_coverage();

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
    source_connection_generation bigint NOT NULL CHECK (source_connection_generation > 0),
    target_connection_generation bigint NOT NULL CHECK (target_connection_generation > 0),
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
    UNIQUE (tenant_id, operation_id, plan_id),
    UNIQUE (
        tenant_id, operation_id, plan_id,
        source_connection_generation, target_connection_generation),
    CONSTRAINT sync_operations_plan_generation_fkey
        FOREIGN KEY (
            tenant_id, plan_id,
            source_connection_generation, target_connection_generation)
        REFERENCES entitysync.sync_plans (
            tenant_id, plan_id,
            source_connection_generation, target_connection_generation),
    CONSTRAINT sync_operations_approval_plan_generation_fkey
        FOREIGN KEY (
            tenant_id, approval_id, plan_id,
            source_connection_generation, target_connection_generation)
        REFERENCES entitysync.sync_approvals (
            tenant_id, approval_id, plan_id,
            source_connection_generation, target_connection_generation),
    CONSTRAINT sync_operations_mode_check CHECK (mode IN ('DryRun','Apply')),
    CONSTRAINT sync_operations_status_check
        CHECK (status IN ('Queued','Leased','Running','Succeeded','Partial','Failed','Cancelled')),
    CONSTRAINT sync_operations_apply_approval_check
        CHECK (mode <> 'Apply' OR approval_id IS NOT NULL),
    CONSTRAINT sync_operations_attempt_check CHECK (attempt >= 0),
    CONSTRAINT sync_operations_lease_check
        CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS sync_operations_apply_approval_uidx
    ON entitysync.sync_operations (tenant_id, approval_id)
    WHERE mode = 'Apply';
CREATE INDEX IF NOT EXISTS sync_operations_tenant_status_lease_idx
    ON entitysync.sync_operations (tenant_id, status, lease_expires_at);
CREATE INDEX IF NOT EXISTS sync_operations_tenant_plan_idx
    ON entitysync.sync_operations (tenant_id, plan_id);

CREATE TABLE IF NOT EXISTS entitysync.sync_operation_items (
    tenant_id text NOT NULL,
    operation_id uuid NOT NULL,
    plan_id uuid NOT NULL,
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
    before_payload_sha256 char(64),
    desired_payload_sha256 char(64) NOT NULL,
    after_payload_sha256 char(64),
    snapshots_expires_at timestamptz NOT NULL,
    vendor_request_id text,
    outcome text NOT NULL,
    error_code text,
    error_message text,
    started_at timestamptz,
    completed_at timestamptz,
    PRIMARY KEY (tenant_id, operation_id, item_id),
    UNIQUE (tenant_id, operation_id, item_id, snapshots_expires_at),
    CONSTRAINT sync_operation_items_operation_plan_fkey
        FOREIGN KEY (tenant_id, operation_id, plan_id)
        REFERENCES entitysync.sync_operations (tenant_id, operation_id, plan_id),
    CONSTRAINT sync_operation_items_plan_item_fkey
        FOREIGN KEY (tenant_id, plan_id, item_id)
        REFERENCES entitysync.sync_plan_items (tenant_id, plan_id, item_id),
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

CREATE TABLE IF NOT EXISTS entitysync.sync_operation_item_snapshots (
    tenant_id text NOT NULL,
    operation_id uuid NOT NULL,
    item_id uuid NOT NULL,
    encrypted_before_ciphertext text,
    encrypted_after_ciphertext text,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, operation_id, item_id),
    CONSTRAINT sync_operation_item_snapshots_item_fkey
        FOREIGN KEY (tenant_id, operation_id, item_id, expires_at)
        REFERENCES entitysync.sync_operation_items (
            tenant_id, operation_id, item_id, snapshots_expires_at),
    CONSTRAINT sync_operation_item_snapshots_values_check
        CHECK (encrypted_before_ciphertext IS NOT NULL OR encrypted_after_ciphertext IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS sync_operation_item_snapshots_expiry_idx
    ON entitysync.sync_operation_item_snapshots (tenant_id, expires_at);

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_plan_immutability()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rows in %.% cannot be deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;
    IF NEW.source_connection_id IS DISTINCT FROM OLD.source_connection_id
        OR NEW.target_connection_id IS DISTINCT FROM OLD.target_connection_id
        OR NEW.source_search IS DISTINCT FROM OLD.source_search
        OR NEW.source_count IS DISTINCT FROM OLD.source_count
        OR NEW.source_entity_id IS DISTINCT FROM OLD.source_entity_id
        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
        OR NEW.policy_id IS DISTINCT FROM OLD.policy_id
        OR NEW.policy_version IS DISTINCT FROM OLD.policy_version
        OR NEW.route_scope IS DISTINCT FROM OLD.route_scope
        OR NEW.source_connection_generation IS DISTINCT FROM OLD.source_connection_generation
        OR NEW.target_connection_generation IS DISTINCT FROM OLD.target_connection_generation
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

CREATE OR REPLACE FUNCTION entitysync.validate_sync_plan_connections()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.source_connection_generation <= 0 OR NEW.target_connection_generation <= 0 THEN
        RETURN NEW;
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM entitysync.connection_definitions
         WHERE tenant_id = NEW.tenant_id
           AND connection_id = NEW.source_connection_id
           AND generation = NEW.source_connection_generation)
       OR NOT EXISTS (
        SELECT 1
          FROM entitysync.connection_definitions
         WHERE tenant_id = NEW.tenant_id
           AND connection_id = NEW.target_connection_id
           AND generation = NEW.target_connection_generation) THEN
        RAISE EXCEPTION 'Plan connection IDs and generations must reference existing connections'
            USING ERRCODE = '23503';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_plan_item_immutability()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Rows in %.% are immutable', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    PERFORM 1
      FROM entitysync.sync_plans
     WHERE tenant_id = NEW.tenant_id AND plan_id = NEW.plan_id
     FOR UPDATE;
    IF NOT EXISTS (
        SELECT 1
          FROM entitysync.sync_plans
         WHERE tenant_id = NEW.tenant_id
           AND plan_id = NEW.plan_id
           AND source_connection_id = NEW.source_connection_id
           AND target_connection_id = NEW.target_connection_id) THEN
        RAISE EXCEPTION 'Plan item connection IDs must match its plan'
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM entitysync.sync_plan_inspections
         WHERE tenant_id = NEW.tenant_id AND plan_id = NEW.plan_id) THEN
        RAISE EXCEPTION 'Inspected plan items cannot be extended'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.lock_sync_plan_for_inspection()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.status <> 'Open' OR NEW.completed_at IS NOT NULL THEN
        RAISE EXCEPTION 'Inspection sessions must start open'
            USING ERRCODE = '55000';
    END IF;

    PERFORM 1
      FROM entitysync.sync_plans
     WHERE tenant_id = NEW.tenant_id AND plan_id = NEW.plan_id
     FOR UPDATE;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_operation_item_binding()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rows in %.% cannot be deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;

    IF TG_OP = 'UPDATE' AND (
        NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.operation_id IS DISTINCT FROM OLD.operation_id
        OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
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
        OR NEW.before_payload_sha256 IS DISTINCT FROM OLD.before_payload_sha256
        OR NEW.desired_payload_sha256 IS DISTINCT FROM OLD.desired_payload_sha256
        OR NEW.snapshots_expires_at IS DISTINCT FROM OLD.snapshots_expires_at) THEN
        RAISE EXCEPTION 'Operation item identity and planned input are immutable'
            USING ERRCODE = '55000';
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM entitysync.sync_operations operation
          JOIN entitysync.sync_plan_items plan_item
            ON plan_item.tenant_id = operation.tenant_id
           AND plan_item.plan_id = operation.plan_id
         WHERE operation.tenant_id = NEW.tenant_id
           AND operation.operation_id = NEW.operation_id
           AND operation.plan_id = NEW.plan_id
           AND NEW.snapshots_expires_at <= operation.created_at + interval '365 days'
           AND plan_item.item_id = NEW.item_id
           AND plan_item.source_vendor = NEW.source_vendor
           AND plan_item.source_connection_id = NEW.source_connection_id
           AND plan_item.source_entity_type = NEW.source_entity_type
           AND plan_item.source_entity_key = NEW.source_entity_key
           AND plan_item.source_entity_id = NEW.source_entity_id
           AND plan_item.target_vendor = NEW.target_vendor
           AND plan_item.target_connection_id = NEW.target_connection_id
           AND plan_item.target_entity_type = NEW.target_entity_type
           AND plan_item.target_entity_id IS NOT DISTINCT FROM NEW.target_entity_id
           AND plan_item.action = NEW.action
           AND plan_item.redacted_before = NEW.redacted_before
           AND plan_item.redacted_desired = NEW.redacted_desired
           AND plan_item.before_payload_sha256 IS NOT DISTINCT FROM NEW.before_payload_sha256
           AND plan_item.desired_payload_sha256 = NEW.desired_payload_sha256) THEN
        RAISE EXCEPTION 'Operation item does not match its approved plan item'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_expired_ciphertext_deletion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'UPDATE' OR OLD.expires_at > now() THEN
        RAISE EXCEPTION 'Ciphertext is immutable until its retention period expires'
            USING ERRCODE = '55000';
    END IF;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS sync_plans_connections ON entitysync.sync_plans;
CREATE TRIGGER sync_plans_connections
    BEFORE INSERT ON entitysync.sync_plans
    FOR EACH ROW EXECUTE FUNCTION entitysync.validate_sync_plan_connections();

DROP TRIGGER IF EXISTS sync_plans_immutable ON entitysync.sync_plans;
CREATE TRIGGER sync_plans_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_plans
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_plan_immutability();

DROP TRIGGER IF EXISTS sync_plan_items_immutable ON entitysync.sync_plan_items;
CREATE TRIGGER sync_plan_items_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON entitysync.sync_plan_items
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_plan_item_immutability();

DROP TRIGGER IF EXISTS sync_plan_inspections_lock ON entitysync.sync_plan_inspections;
CREATE TRIGGER sync_plan_inspections_lock
    BEFORE INSERT ON entitysync.sync_plan_inspections
    FOR EACH ROW EXECUTE FUNCTION entitysync.lock_sync_plan_for_inspection();

DROP TRIGGER IF EXISTS sync_plan_inspections_completion ON entitysync.sync_plan_inspections;
CREATE TRIGGER sync_plan_inspections_completion
    BEFORE UPDATE OR DELETE ON entitysync.sync_plan_inspections
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_inspection_completion();

DROP TRIGGER IF EXISTS sync_plan_inspection_ranges_open
    ON entitysync.sync_plan_inspection_ranges;
CREATE TRIGGER sync_plan_inspection_ranges_open
    BEFORE INSERT ON entitysync.sync_plan_inspection_ranges
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_open_inspection_range();

DROP TRIGGER IF EXISTS sync_plan_inspection_ranges_immutable
    ON entitysync.sync_plan_inspection_ranges;
CREATE TRIGGER sync_plan_inspection_ranges_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_plan_inspection_ranges
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();


DROP TRIGGER IF EXISTS sync_approvals_immutable ON entitysync.sync_approvals;
CREATE TRIGGER sync_approvals_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_approvals
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();

DROP TRIGGER IF EXISTS sync_operation_items_bound ON entitysync.sync_operation_items;
CREATE TRIGGER sync_operation_items_bound
    BEFORE INSERT OR UPDATE OR DELETE ON entitysync.sync_operation_items
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_operation_item_binding();

DROP TRIGGER IF EXISTS sync_operation_item_snapshots_retention
    ON entitysync.sync_operation_item_snapshots;
CREATE TRIGGER sync_operation_item_snapshots_retention
    BEFORE UPDATE OR DELETE ON entitysync.sync_operation_item_snapshots
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_expired_ciphertext_deletion();
