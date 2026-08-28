CREATE TABLE IF NOT EXISTS entitysync.entity_change_state (
    tenant_id text NOT NULL,
    route_scope char(64) NOT NULL,
    source_vendor text NOT NULL,
    source_connection_id text NOT NULL,
    source_entity_type text NOT NULL,
    target_vendor text NOT NULL,
    target_connection_id text NOT NULL,
    target_entity_type text NOT NULL,
    source_entity_key varchar(512) NOT NULL,
    source_entity_id varchar(512) NOT NULL,
    source_name varchar(512) NOT NULL,
    target_entity_id varchar(512) NOT NULL,
    hash_version integer NOT NULL,
    payload_hash char(64) NOT NULL,
    applied_at timestamptz NOT NULL,
    PRIMARY KEY (
        tenant_id,
        route_scope,
        source_vendor,
        source_connection_id,
        source_entity_type,
        target_vendor,
        target_connection_id,
        target_entity_type,
        source_entity_key
    ),
    CONSTRAINT entity_change_state_indexed_identity_size CHECK (
        octet_length(tenant_id) +
        octet_length(route_scope) +
        octet_length(source_vendor) +
        octet_length(source_connection_id) +
        octet_length(source_entity_type) +
        octet_length(target_vendor) +
        octet_length(target_connection_id) +
        octet_length(target_entity_type) +
        octet_length(source_entity_key) <= 2000
    ),
    CONSTRAINT entity_change_state_source_not_blank CHECK (btrim(source_entity_id) <> ''),
    CONSTRAINT entity_change_state_target_not_blank CHECK (btrim(target_entity_id) <> ''),
    CONSTRAINT entity_change_state_hash_version_positive CHECK (hash_version > 0),
    CONSTRAINT entity_change_state_scope_hex CHECK (route_scope ~ '^[0-9a-f]{64}$'),
    CONSTRAINT entity_change_state_payload_hex CHECK (payload_hash ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS entity_change_state_route_idx
    ON entitysync.entity_change_state (
        tenant_id,
        route_scope,
        source_vendor,
        source_connection_id,
        source_entity_type,
        target_vendor,
        target_connection_id,
        target_entity_type
    );
