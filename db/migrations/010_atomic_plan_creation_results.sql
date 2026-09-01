ALTER TABLE entitysync.sync_plan_creation_claims
    ADD COLUMN IF NOT EXISTS result_plan_digest_sha256 char(64);

ALTER TABLE entitysync.sync_plan_creation_claims
    DROP CONSTRAINT IF EXISTS sync_plan_creation_claims_result_check;

-- Claims completed before this migration did not bind the result digest atomically with
-- manifest insertion. Reopen them as expired rather than inferring trust from PlanId alone.
UPDATE entitysync.sync_plan_creation_claims
SET state = 'InProgress',
    result_plan_id = NULL,
    result_plan_digest_sha256 = NULL,
    lease_expires_at = clock_timestamp(),
    updated_at = clock_timestamp()
WHERE state = 'Completed';

ALTER TABLE entitysync.sync_plan_creation_claims
    ADD CONSTRAINT sync_plan_creation_claims_result_check
    CHECK (
        (state = 'Completed') =
        (result_plan_id IS NOT NULL AND result_plan_digest_sha256 IS NOT NULL)),
    ADD CONSTRAINT sync_plan_creation_claims_result_digest_check
    CHECK (
        result_plan_digest_sha256 IS NULL
        OR result_plan_digest_sha256 ~ '^[0-9a-f]{64}$');
