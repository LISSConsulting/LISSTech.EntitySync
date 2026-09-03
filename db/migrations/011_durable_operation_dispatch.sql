ALTER TABLE entitysync.sync_operations
    ADD COLUMN IF NOT EXISTS request_sha256 char(64),
    ADD COLUMN IF NOT EXISTS total_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS succeeded_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS failed_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS skipped_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS unknown_count integer NOT NULL DEFAULT 0;

ALTER TABLE entitysync.sync_operations
    DROP CONSTRAINT IF EXISTS sync_operations_request_sha256_check;
ALTER TABLE entitysync.sync_operations
    ADD CONSTRAINT sync_operations_request_sha256_check
        CHECK (request_sha256 IS NULL OR request_sha256 ~ '^[0-9a-f]{64}$');

ALTER TABLE entitysync.sync_operation_items
    ADD COLUMN IF NOT EXISTS dispatch_started_at timestamptz,
    ADD COLUMN IF NOT EXISTS vendor_target_entity_id text,
    ADD COLUMN IF NOT EXISTS safe_write_code text,
    ADD COLUMN IF NOT EXISTS reconcile_lease_owner text,
    ADD COLUMN IF NOT EXISTS reconcile_lease_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS reconcile_attempt integer NOT NULL DEFAULT 0;

ALTER TABLE entitysync.sync_operation_items
    DROP CONSTRAINT IF EXISTS sync_operation_items_reconcile_lease_check;
ALTER TABLE entitysync.sync_operation_items
    ADD CONSTRAINT sync_operation_items_reconcile_lease_check
        CHECK ((reconcile_lease_owner IS NULL) = (reconcile_lease_expires_at IS NULL));

ALTER TABLE entitysync.sync_operation_items
    DROP CONSTRAINT IF EXISTS sync_operation_items_dispatch_boundary_check;
ALTER TABLE entitysync.sync_operation_items
    ADD CONSTRAINT sync_operation_items_dispatch_boundary_check
        CHECK (dispatch_started_at IS NULL OR vendor_request_id IS NOT NULL);

CREATE INDEX IF NOT EXISTS sync_operation_items_unknown_reconcile_idx
    ON entitysync.sync_operation_items (
        tenant_id, outcome, reconcile_lease_expires_at, operation_id, item_id)
    WHERE outcome = 'Unknown';

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_operation_item_binding()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Rows in %.% cannot be deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
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
            OR NEW.redacted_desired IS DISTINCT FROM OLD.redacted_desired
            OR NEW.desired_payload_sha256 IS DISTINCT FROM OLD.desired_payload_sha256
            OR NEW.snapshots_expires_at IS DISTINCT FROM OLD.snapshots_expires_at THEN
            RAISE EXCEPTION 'Sync operation item identity and planned intent are immutable'
                USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM entitysync.sync_plan_items AS plan_item
        WHERE plan_item.tenant_id = NEW.tenant_id
          AND plan_item.plan_id = NEW.plan_id
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
        RAISE EXCEPTION 'Sync operation item does not match its immutable plan item'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_sync_operation_snapshot_retention()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
           OR NEW.operation_id IS DISTINCT FROM OLD.operation_id
           OR NEW.item_id IS DISTINCT FROM OLD.item_id
           OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
           OR (OLD.encrypted_before_ciphertext IS NOT NULL
               AND NEW.encrypted_before_ciphertext IS DISTINCT FROM OLD.encrypted_before_ciphertext)
           OR (OLD.encrypted_after_ciphertext IS NOT NULL
               AND NEW.encrypted_after_ciphertext IS DISTINCT FROM OLD.encrypted_after_ciphertext)
           OR (OLD.encrypted_before_ciphertext IS NULL
               AND NEW.encrypted_before_ciphertext IS NULL
               AND OLD.encrypted_after_ciphertext IS NULL
               AND NEW.encrypted_after_ciphertext IS NULL) THEN
            RAISE EXCEPTION 'Persisted operation snapshot ciphertext is immutable'
                USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.expires_at > now() THEN
        RAISE EXCEPTION 'Ciphertext is immutable until its retention period expires'
            USING ERRCODE = '55000';
    END IF;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS sync_operation_item_snapshots_retention
    ON entitysync.sync_operation_item_snapshots;
CREATE TRIGGER sync_operation_item_snapshots_retention
    BEFORE UPDATE OR DELETE ON entitysync.sync_operation_item_snapshots
    FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_sync_operation_snapshot_retention();
