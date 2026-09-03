CREATE TABLE IF NOT EXISTS entitysync.connection_definitions (
    tenant_id text NOT NULL,
    connection_id text NOT NULL,
    vendor text NOT NULL,
    display_name text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    enabled boolean NOT NULL,
    public_configuration jsonb NOT NULL DEFAULT '{}'::jsonb,
    secret_ciphertext text NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL,
    PRIMARY KEY (tenant_id, connection_id),
    UNIQUE (tenant_id, vendor, connection_id)
);

CREATE INDEX IF NOT EXISTS connection_definitions_tenant_vendor_enabled_idx
    ON entitysync.connection_definitions (tenant_id, vendor, enabled);

CREATE TABLE IF NOT EXISTS entitysync.sync_policies (
    tenant_id text NOT NULL,
    policy_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    name text NOT NULL,
    route_scope text NOT NULL,
    definition jsonb NOT NULL,
    definition_sha256 char(64) NOT NULL,
    enabled boolean NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    PRIMARY KEY (tenant_id, policy_id, version),
    UNIQUE (tenant_id, name, version),
    CONSTRAINT sync_policies_definition_sha256_check
        CHECK (definition_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS sync_policies_tenant_route_enabled_idx
    ON entitysync.sync_policies (tenant_id, route_scope, enabled);

CREATE OR REPLACE FUNCTION entitysync.reject_immutable_row_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Rows in %.% are immutable', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = '55000';
END;
$$;

DROP TRIGGER IF EXISTS sync_policies_immutable ON entitysync.sync_policies;
CREATE TRIGGER sync_policies_immutable
    BEFORE UPDATE OR DELETE ON entitysync.sync_policies
    FOR EACH ROW EXECUTE FUNCTION entitysync.reject_immutable_row_mutation();
