CREATE TABLE IF NOT EXISTS entitysync.plan_import_receipts (
    tenant_id text NOT NULL,
    caller_key text NOT NULL,
    request_sha256 char(64) NOT NULL,
    actor_id text NOT NULL,
    plan_id uuid NOT NULL,
    plan_digest_sha256 char(64) NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, caller_key),
    CONSTRAINT plan_import_receipts_request_sha256_check
        CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT plan_import_receipts_plan_digest_sha256_check
        CHECK (plan_digest_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT plan_import_receipts_caller_key_check
        CHECK (length(btrim(caller_key)) > 0),
    CONSTRAINT plan_import_receipts_actor_id_check
        CHECK (length(btrim(actor_id)) > 0),
    CONSTRAINT plan_import_receipts_plan_fk
        FOREIGN KEY (tenant_id, plan_id)
        REFERENCES entitysync.sync_plans (tenant_id, plan_id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS plan_import_receipts_expiry_idx
    ON entitysync.plan_import_receipts (tenant_id, expires_at);
