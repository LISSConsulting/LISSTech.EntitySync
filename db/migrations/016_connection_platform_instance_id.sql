ALTER TABLE entitysync.connection_definitions
    ADD COLUMN IF NOT EXISTS platform_instance_id uuid;

ALTER TABLE entitysync.connection_definitions
    DROP CONSTRAINT IF EXISTS connection_definitions_platform_instance_id_check;
ALTER TABLE entitysync.connection_definitions
    ADD CONSTRAINT connection_definitions_platform_instance_id_check
    CHECK (
        platform_instance_id IS NULL
        OR platform_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid
    );

CREATE UNIQUE INDEX IF NOT EXISTS connection_definitions_platform_instance_id_uniq
    ON entitysync.connection_definitions (tenant_id, platform_instance_id)
    WHERE platform_instance_id IS NOT NULL;
