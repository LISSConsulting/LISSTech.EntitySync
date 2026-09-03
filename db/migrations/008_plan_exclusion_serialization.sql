CREATE TABLE IF NOT EXISTS entitysync.sync_plan_creation_claims (
    tenant_id text NOT NULL,
    plan_id uuid NOT NULL,
    request_sha256 char(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, plan_id),
    CONSTRAINT sync_plan_creation_claims_request_sha256_check
        CHECK (request_sha256 ~ '^[0-9a-f]{64}$')
);

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
               max(range_end) OVER (
                   ORDER BY range_start, range_end
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS previous_max_end
          FROM entitysync.sync_plan_inspection_ranges
         WHERE tenant_id = NEW.tenant_id AND inspection_id = NEW.inspection_id
    )
    SELECT count(*) FILTER (
               WHERE (position = 1 AND range_start <> 0)
                  OR (position > 1 AND range_start <= previous_max_end)
                  OR (position > 1 AND range_start > previous_max_end + 1)
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
        RAISE EXCEPTION 'Inspection ranges must exactly cover every plan ordinal without gaps'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.entity_route_lock_key(
    tenant_id text,
    source_connection_id text,
    target_connection_id text)
RETURNS bigint
LANGUAGE sql
IMMUTABLE
STRICT
AS $$
    SELECT hashtextextended(
        concat_ws(chr(31), tenant_id, source_connection_id, target_connection_id),
        0);
$$;

CREATE OR REPLACE FUNCTION entitysync.lock_sync_plan_route()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(entitysync.entity_route_lock_key(
        NEW.tenant_id,
        NEW.source_connection_id,
        NEW.target_connection_id));
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_plans_route_lock ON entitysync.sync_plans;
CREATE TRIGGER sync_plans_route_lock
BEFORE INSERT ON entitysync.sync_plans
FOR EACH ROW EXECUTE FUNCTION entitysync.lock_sync_plan_route();

CREATE OR REPLACE FUNCTION entitysync.enforce_exclusion_against_plans()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(entitysync.entity_route_lock_key(
        NEW.tenant_id,
        NEW.source_connection_id,
        NEW.target_connection_id));

    IF NEW.revoked_at IS NULL AND EXISTS (
        SELECT 1
        FROM entitysync.sync_plans AS plan
        JOIN entitysync.sync_plan_items AS item
          ON item.tenant_id = plan.tenant_id
         AND item.plan_id = plan.plan_id
        WHERE plan.tenant_id = NEW.tenant_id
          AND plan.source_connection_id = NEW.source_connection_id
          AND plan.target_connection_id = NEW.target_connection_id
          AND item.source_vendor = NEW.source_vendor
          AND item.source_entity_type = NEW.source_entity_type
          AND item.target_vendor = NEW.target_vendor
          AND item.target_entity_type = NEW.target_entity_type
          AND plan.status = 'Approved'
          AND plan.expires_at > now()
          AND lower(item.source_entity_id) = NEW.source_entity_key
          AND item.action <> 'None') THEN
        RAISE EXCEPTION 'An active durable plan already contains this entity.'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS entity_exclusions_plan_guard ON entitysync.entity_exclusions;
CREATE TRIGGER entity_exclusions_plan_guard
BEFORE INSERT OR UPDATE ON entitysync.entity_exclusions
FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_exclusion_against_plans();

CREATE OR REPLACE FUNCTION entitysync.enforce_plan_item_exclusion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    plan_row entitysync.sync_plans%ROWTYPE;
BEGIN
    SELECT * INTO STRICT plan_row
    FROM entitysync.sync_plans
    WHERE tenant_id = NEW.tenant_id
      AND plan_id = NEW.plan_id;

    PERFORM pg_advisory_xact_lock(entitysync.entity_route_lock_key(
        plan_row.tenant_id,
        plan_row.source_connection_id,
        plan_row.target_connection_id));

    IF NEW.action <> 'None' AND EXISTS (
        SELECT 1
        FROM entitysync.entity_exclusions AS exclusion
        WHERE exclusion.tenant_id = plan_row.tenant_id
          AND exclusion.source_vendor = NEW.source_vendor
          AND exclusion.source_connection_id = plan_row.source_connection_id
          AND exclusion.source_entity_type = NEW.source_entity_type
          AND exclusion.target_vendor = NEW.target_vendor
          AND exclusion.target_connection_id = plan_row.target_connection_id
          AND exclusion.target_entity_type = NEW.target_entity_type
          AND exclusion.source_entity_key = lower(NEW.source_entity_id)
          AND exclusion.revoked_at IS NULL) THEN
        RAISE EXCEPTION 'The durable plan contains an actively excluded entity.'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_plan_items_exclusion_guard ON entitysync.sync_plan_items;
CREATE TRIGGER sync_plan_items_exclusion_guard
BEFORE INSERT ON entitysync.sync_plan_items
FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_plan_item_exclusion();

CREATE OR REPLACE FUNCTION entitysync.enforce_approval_exclusions()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.status = 'Approved' AND OLD.status <> 'Approved' THEN
        PERFORM pg_advisory_xact_lock(entitysync.entity_route_lock_key(
            NEW.tenant_id,
            NEW.source_connection_id,
            NEW.target_connection_id));

        IF EXISTS (
            SELECT 1
            FROM entitysync.sync_plan_items AS item
            JOIN entitysync.entity_exclusions AS exclusion
              ON exclusion.tenant_id = NEW.tenant_id
             AND exclusion.source_vendor = item.source_vendor
             AND exclusion.source_connection_id = NEW.source_connection_id
             AND exclusion.source_entity_type = item.source_entity_type
             AND exclusion.target_vendor = item.target_vendor
             AND exclusion.target_connection_id = NEW.target_connection_id
             AND exclusion.target_entity_type = item.target_entity_type
             AND exclusion.source_entity_key = lower(item.source_entity_id)
             AND exclusion.revoked_at IS NULL
            WHERE item.tenant_id = NEW.tenant_id
              AND item.plan_id = NEW.plan_id
              AND item.action <> 'None') THEN
            RAISE EXCEPTION 'The durable plan contains an actively excluded entity.'
                USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS sync_plans_approval_exclusion_guard ON entitysync.sync_plans;
CREATE TRIGGER sync_plans_approval_exclusion_guard
BEFORE UPDATE ON entitysync.sync_plans
FOR EACH ROW EXECUTE FUNCTION entitysync.enforce_approval_exclusions();
