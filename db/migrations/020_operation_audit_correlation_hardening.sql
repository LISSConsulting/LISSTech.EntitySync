DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM entitysync.sync_operation_items item
        JOIN entitysync.sync_plan_items plan_item
          ON plan_item.tenant_id = item.tenant_id
         AND plan_item.plan_id = item.plan_id
         AND plan_item.item_id = item.item_id
        WHERE item.item_index IS DISTINCT FROM plan_item.item_ordinal
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'Existing operation item_index does not match its authoritative plan item ordinal.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM entitysync.sync_operations
        WHERE operation_id = plan_id
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'Existing operation_id and plan_id audit identities are not distinct.';
    END IF;
END
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
        OR NEW.item_index IS DISTINCT FROM OLD.item_index
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
           AND plan_item.item_ordinal = NEW.item_index
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

ALTER TABLE entitysync.sync_operations
    DROP CONSTRAINT IF EXISTS sync_operations_audit_correlation_check;

ALTER TABLE entitysync.sync_operations
    ADD CONSTRAINT sync_operations_audit_correlation_check
    CHECK (
        operation_id <> plan_id
        AND (
            (run_id IS NULL
             AND correlation_id IS NULL
             AND status IN ('Succeeded', 'Partial', 'Failed', 'Cancelled'))
            OR
            (run_id IS NOT NULL
             AND correlation_id IS NOT NULL
             AND run_id <> operation_id
             AND run_id <> plan_id
             AND correlation_id <> operation_id
             AND correlation_id <> plan_id
             AND correlation_id <> run_id)));
