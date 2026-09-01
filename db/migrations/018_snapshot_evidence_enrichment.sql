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
        RAISE EXCEPTION 'Inspection ranges must exactly cover every plan ordinal without gaps or overlaps'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION entitysync.enforce_retained_ciphertext_scrub()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Retention rows preserve metadata and cannot be deleted'
            USING ERRCODE = '55000';
    END IF;

    -- An operation snapshot is created before dispatch and enriched with the
    -- authoritative result after reconciliation. Existing ciphertext is
    -- immutable; only a previously-null half may be populated before expiry.
    IF TG_TABLE_NAME = 'sync_operation_item_snapshots' THEN
        IF OLD.expires_at > clock_timestamp()
           AND OLD.values_redacted_at IS NULL
           AND NEW.values_redacted_at IS NULL
           AND NEW.tenant_id IS NOT DISTINCT FROM OLD.tenant_id
           AND NEW.operation_id IS NOT DISTINCT FROM OLD.operation_id
           AND NEW.item_id IS NOT DISTINCT FROM OLD.item_id
           AND NEW.expires_at IS NOT DISTINCT FROM OLD.expires_at
           AND (OLD.encrypted_before_ciphertext IS NULL
                OR NEW.encrypted_before_ciphertext IS NOT DISTINCT FROM OLD.encrypted_before_ciphertext)
           AND (OLD.encrypted_after_ciphertext IS NULL
                OR NEW.encrypted_after_ciphertext IS NOT DISTINCT FROM OLD.encrypted_after_ciphertext)
           AND (NEW.encrypted_before_ciphertext IS NOT NULL
                OR NEW.encrypted_after_ciphertext IS NOT NULL) THEN
            RETURN NEW;
        END IF;
    END IF;

    IF OLD.expires_at > clock_timestamp()
       OR OLD.values_redacted_at IS NOT NULL
       OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.values_redacted_at IS NULL THEN
        RAISE EXCEPTION 'Ciphertext can only be scrubbed once after database expiry'
            USING ERRCODE = '55000';
    END IF;
    IF TG_TABLE_NAME = 'audit_event_full_values' THEN
        IF NEW.audit_event_id IS DISTINCT FROM OLD.audit_event_id
           OR NEW.full_values_ciphertext IS NOT NULL THEN
            RAISE EXCEPTION 'Audit retention scrub cannot alter identity or metadata'
                USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'sync_operation_item_snapshots' THEN
        IF NEW.operation_id IS DISTINCT FROM OLD.operation_id
           OR NEW.item_id IS DISTINCT FROM OLD.item_id
           OR NEW.encrypted_before_ciphertext IS NOT NULL
           OR NEW.encrypted_after_ciphertext IS NOT NULL THEN
            RAISE EXCEPTION 'Operation snapshot scrub cannot alter identity or metadata'
                USING ERRCODE = '55000';
        END IF;
    ELSE
        RAISE EXCEPTION 'Unsupported retention table'
            USING ERRCODE = '55000';
    END IF;
    NEW.values_redacted_at := clock_timestamp();
    RETURN NEW;
END;
$$;
