ALTER TABLE entitysync.sync_control_work
    ADD COLUMN IF NOT EXISTS requested_by text;

UPDATE entitysync.sync_control_work
SET requested_by = 'entitysync-control-worker'
WHERE requested_by IS NULL;

ALTER TABLE entitysync.sync_control_work
    ALTER COLUMN requested_by SET DEFAULT 'entitysync-control-worker',
    ALTER COLUMN requested_by SET NOT NULL;

ALTER TABLE entitysync.sync_control_work
    DROP CONSTRAINT IF EXISTS sync_control_work_requested_by_check;
ALTER TABLE entitysync.sync_control_work
    ADD CONSTRAINT sync_control_work_requested_by_check
        CHECK (length(btrim(requested_by)) BETWEEN 1 AND 500);
