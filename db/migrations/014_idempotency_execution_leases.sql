ALTER TABLE entitysync.api_idempotency_records
    ALTER COLUMN response_body TYPE text USING response_body::text;

ALTER TABLE entitysync.api_idempotency_records
    DROP CONSTRAINT IF EXISTS api_idempotency_records_response_json_check;
ALTER TABLE entitysync.api_idempotency_records
    ADD CONSTRAINT api_idempotency_records_response_json_check CHECK (
        response_body IS NULL OR response_body::jsonb IS NOT NULL);

ALTER TABLE entitysync.api_idempotency_records
    ADD COLUMN IF NOT EXISTS execution_owner uuid,
    ADD COLUMN IF NOT EXISTS execution_attempt bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS execution_lease_expires_at timestamptz;

UPDATE entitysync.api_idempotency_records
SET execution_owner = NULL,
    execution_lease_expires_at = NULL
WHERE completed_at IS NOT NULL
   OR response_status_code IS NOT NULL
   OR response_body IS NOT NULL;

ALTER TABLE entitysync.api_idempotency_records
    DROP CONSTRAINT IF EXISTS api_idempotency_records_execution_lease_check;
ALTER TABLE entitysync.api_idempotency_records
    ADD CONSTRAINT api_idempotency_records_execution_lease_check CHECK (
        (execution_owner IS NULL) = (execution_lease_expires_at IS NULL)
        AND execution_attempt >= 0);

CREATE INDEX IF NOT EXISTS api_idempotency_records_live_lease_idx
    ON entitysync.api_idempotency_records (
        tenant_id, idempotency_key, execution_lease_expires_at)
    WHERE completed_at IS NULL;

ALTER TABLE entitysync.sync_policies
    ADD COLUMN IF NOT EXISTS idempotency_token char(64);

ALTER TABLE entitysync.sync_policies
    DROP CONSTRAINT IF EXISTS sync_policies_idempotency_token_check;
ALTER TABLE entitysync.sync_policies
    ADD CONSTRAINT sync_policies_idempotency_token_check CHECK (
        idempotency_token IS NULL
        OR idempotency_token ~ '^[0-9a-f]{64}$');

CREATE UNIQUE INDEX IF NOT EXISTS sync_policies_idempotency_token_uidx
    ON entitysync.sync_policies (tenant_id, policy_id, idempotency_token)
    WHERE idempotency_token IS NOT NULL;
