ALTER TABLE entitysync.sync_operations
    ADD COLUMN IF NOT EXISTS run_id uuid,
    ADD COLUMN IF NOT EXISTS correlation_id uuid;

ALTER TABLE entitysync.sync_operation_items
    ADD COLUMN IF NOT EXISTS item_index integer;

UPDATE entitysync.sync_operation_items item
SET item_index = plan_item.item_ordinal
FROM entitysync.sync_plan_items plan_item
WHERE item.item_index IS NULL
  AND plan_item.tenant_id = item.tenant_id
  AND plan_item.plan_id = item.plan_id
  AND plan_item.item_id = item.item_id;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM entitysync.sync_operation_items
        WHERE item_index IS NULL
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'Cannot derive operation item_index from the authoritative plan item ordinal.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM entitysync.sync_operations operation
        WHERE (operation.status NOT IN ('Succeeded', 'Partial', 'Failed', 'Cancelled')
               OR EXISTS (
                   SELECT 1
                   FROM entitysync.sync_operation_items item
                   WHERE item.tenant_id = operation.tenant_id
                     AND item.operation_id = operation.operation_id
                     AND item.outcome IN ('Pending', 'Unknown')))
          AND (operation.run_id IS NULL OR operation.correlation_id IS NULL)
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'Replayable legacy operations have no authoritative run/correlation identity.';
    END IF;
END
$$;

ALTER TABLE entitysync.sync_operation_items
    ALTER COLUMN item_index SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE connamespace = 'entitysync'::regnamespace
          AND conname = 'sync_operation_items_item_index_check'
    ) THEN
        ALTER TABLE entitysync.sync_operation_items
            ADD CONSTRAINT sync_operation_items_item_index_check
            CHECK (item_index >= 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE connamespace = 'entitysync'::regnamespace
          AND conname = 'sync_operations_audit_correlation_check'
    ) THEN
        ALTER TABLE entitysync.sync_operations
            ADD CONSTRAINT sync_operations_audit_correlation_check
            CHECK (
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
                 AND correlation_id <> run_id));
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS sync_operations_tenant_run_uidx
    ON entitysync.sync_operations (tenant_id, run_id)
    WHERE run_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS sync_operation_items_tenant_index_uidx
    ON entitysync.sync_operation_items (tenant_id, operation_id, item_index);
