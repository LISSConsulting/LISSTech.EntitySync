ALTER TABLE entitysync.sync_plan_creation_claims
    ADD COLUMN IF NOT EXISTS owner_token uuid,
    ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS state text,
    ADD COLUMN IF NOT EXISTS result_plan_id uuid,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz;

UPDATE entitysync.sync_plan_creation_claims AS claim
SET owner_token = COALESCE(
        claim.owner_token,
        md5(claim.tenant_id || ':' || claim.plan_id::text)::uuid),
    lease_expires_at = COALESCE(claim.lease_expires_at, claim.created_at),
    state = COALESCE(
        claim.state,
        CASE WHEN EXISTS (
            SELECT 1
            FROM entitysync.sync_plans AS plan
            WHERE plan.tenant_id = claim.tenant_id
              AND plan.plan_id = claim.plan_id
        ) THEN 'Completed' ELSE 'InProgress' END),
    result_plan_id = COALESCE(
        claim.result_plan_id,
        CASE WHEN EXISTS (
            SELECT 1
            FROM entitysync.sync_plans AS plan
            WHERE plan.tenant_id = claim.tenant_id
              AND plan.plan_id = claim.plan_id
        ) THEN claim.plan_id END),
    updated_at = COALESCE(claim.updated_at, claim.created_at);
ALTER TABLE entitysync.sync_plan_creation_claims
    ALTER COLUMN owner_token SET NOT NULL,
    ALTER COLUMN lease_expires_at SET NOT NULL,
    ALTER COLUMN state SET NOT NULL,
    ALTER COLUMN updated_at SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'sync_plan_creation_claims_state_check'
          AND conrelid = 'entitysync.sync_plan_creation_claims'::regclass
    ) THEN
        ALTER TABLE entitysync.sync_plan_creation_claims
            ADD CONSTRAINT sync_plan_creation_claims_state_check
            CHECK (state IN ('InProgress', 'Completed'));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'sync_plan_creation_claims_result_check'
          AND conrelid = 'entitysync.sync_plan_creation_claims'::regclass
    ) THEN
        ALTER TABLE entitysync.sync_plan_creation_claims
            ADD CONSTRAINT sync_plan_creation_claims_result_check
            CHECK ((state = 'Completed') = (result_plan_id IS NOT NULL));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'sync_plan_creation_claims_result_key'
          AND conrelid = 'entitysync.sync_plan_creation_claims'::regclass
    ) THEN
        ALTER TABLE entitysync.sync_plan_creation_claims
            ADD CONSTRAINT sync_plan_creation_claims_result_key
            FOREIGN KEY (tenant_id, result_plan_id)
            REFERENCES entitysync.sync_plans (tenant_id, plan_id);
    END IF;
END
$$;

CREATE INDEX IF NOT EXISTS sync_plan_creation_claims_lease_idx
    ON entitysync.sync_plan_creation_claims (lease_expires_at)
    WHERE state = 'InProgress';
