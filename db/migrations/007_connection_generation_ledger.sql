CREATE TABLE IF NOT EXISTS entitysync.connection_generation_counters (
    tenant_id text NOT NULL,
    connection_id text NOT NULL,
    last_generation bigint NOT NULL CHECK (last_generation > 0),
    PRIMARY KEY (tenant_id, connection_id)
);

INSERT INTO entitysync.connection_generation_counters (
    tenant_id, connection_id, last_generation)
SELECT tenant_id, connection_id, max(generation)
FROM entitysync.connection_definitions
GROUP BY tenant_id, connection_id
ON CONFLICT (tenant_id, connection_id) DO UPDATE
SET last_generation = GREATEST(
    entitysync.connection_generation_counters.last_generation,
    EXCLUDED.last_generation);

CREATE OR REPLACE FUNCTION entitysync.reject_generation_counter_regression()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Connection generation counters cannot be deleted'
            USING ERRCODE = '55000';
    END IF;
    IF NEW.last_generation < OLD.last_generation THEN
        RAISE EXCEPTION 'Connection generation counters cannot decrease'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS connection_generation_counters_monotonic
    ON entitysync.connection_generation_counters;
CREATE TRIGGER connection_generation_counters_monotonic
    BEFORE UPDATE OR DELETE ON entitysync.connection_generation_counters
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_generation_counter_regression();
