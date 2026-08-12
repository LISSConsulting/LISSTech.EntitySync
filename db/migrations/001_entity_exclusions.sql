CREATE SCHEMA IF NOT EXISTS entitysync;

CREATE TABLE IF NOT EXISTS entitysync.schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS entitysync.entity_exclusions (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    source_vendor text NOT NULL,
    source_connection_id text NOT NULL,
    source_entity_type text NOT NULL,
    target_vendor text NOT NULL,
    target_connection_id text NOT NULL,
    target_entity_type text NOT NULL,
    source_entity_key text NOT NULL,
    source_entity_id text NOT NULL,
    source_name text NOT NULL,
    reason text NOT NULL,
    created_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_by text,
    revoked_at timestamptz,
    CONSTRAINT entity_exclusions_source_id_not_blank CHECK (btrim(source_entity_id) <> ''),
    CONSTRAINT entity_exclusions_reason_not_blank CHECK (btrim(reason) <> ''),
    CONSTRAINT entity_exclusions_revocation_complete CHECK ((revoked_by IS NULL) = (revoked_at IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS entity_exclusions_active_route_source_uidx
    ON entitysync.entity_exclusions (
        tenant_id,
        source_vendor,
        source_connection_id,
        source_entity_type,
        target_vendor,
        target_connection_id,
        target_entity_type,
        source_entity_key
    )
    WHERE revoked_at IS NULL;

CREATE INDEX IF NOT EXISTS entity_exclusions_active_route_idx
    ON entitysync.entity_exclusions (
        tenant_id,
        source_vendor,
        source_connection_id,
        source_entity_type,
        target_vendor,
        target_connection_id,
        target_entity_type
    )
    WHERE revoked_at IS NULL;
