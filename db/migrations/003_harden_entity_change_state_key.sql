ALTER TABLE entitysync.entity_change_state
    DROP CONSTRAINT IF EXISTS entity_change_state_pkey;

ALTER TABLE entitysync.entity_change_state
    ALTER COLUMN source_entity_key TYPE varchar(512),
    ALTER COLUMN source_entity_id TYPE varchar(512),
    ALTER COLUMN source_name TYPE varchar(512),
    ALTER COLUMN target_entity_id TYPE varchar(512);

ALTER TABLE entitysync.entity_change_state
    DROP CONSTRAINT IF EXISTS entity_change_state_indexed_identity_size;

ALTER TABLE entitysync.entity_change_state
    ADD CONSTRAINT entity_change_state_indexed_identity_size CHECK (
        octet_length(tenant_id) +
        octet_length(route_scope) +
        octet_length(source_vendor) +
        octet_length(source_connection_id) +
        octet_length(source_entity_type) +
        octet_length(target_vendor) +
        octet_length(target_connection_id) +
        octet_length(target_entity_type) +
        octet_length(source_entity_key) <= 2000
    );

ALTER TABLE entitysync.entity_change_state
    ADD CONSTRAINT entity_change_state_pkey PRIMARY KEY (
        tenant_id,
        route_scope,
        source_vendor,
        source_connection_id,
        source_entity_type,
        target_vendor,
        target_connection_id,
        target_entity_type,
        source_entity_key
    );

