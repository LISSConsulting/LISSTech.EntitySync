CREATE TABLE entitysync.entity_records (
    tenant_id text NOT NULL,
    vendor_key text NOT NULL,
    connection_key text NOT NULL,
    entity_type_key text NOT NULL,
    entity_id_key text NOT NULL,
    vendor text NOT NULL,
    connection_id text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    name text NOT NULL,
    normalized_name text NOT NULL,
    is_active boolean,
    payload jsonb NOT NULL,
    payload_hash text NOT NULL,
    first_observed_at timestamptz NOT NULL,
    last_observed_at timestamptz NOT NULL,
    last_plan_id text,
    PRIMARY KEY (tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key),
    CONSTRAINT entity_records_payload_object CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT entity_records_payload_hash_length CHECK (length(payload_hash) = 64),
    CONSTRAINT entity_records_observation_order CHECK (last_observed_at >= first_observed_at)
);

CREATE INDEX entity_records_name_idx
    ON entitysync.entity_records (tenant_id, normalized_name text_pattern_ops);
CREATE INDEX entity_records_vendor_type_idx
    ON entitysync.entity_records (tenant_id, vendor_key, entity_type_key, last_observed_at DESC);
CREATE INDEX entity_records_payload_idx
    ON entitysync.entity_records USING gin (payload);

CREATE TABLE entitysync.entity_record_versions (
    tenant_id text NOT NULL,
    vendor_key text NOT NULL,
    connection_key text NOT NULL,
    entity_type_key text NOT NULL,
    entity_id_key text NOT NULL,
    payload_hash text NOT NULL,
    payload jsonb NOT NULL,
    first_observed_at timestamptz NOT NULL,
    last_observed_at timestamptz NOT NULL,
    last_plan_id text,
    PRIMARY KEY (
        tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key, payload_hash),
    FOREIGN KEY (tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key)
        REFERENCES entitysync.entity_records (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key)
        ON DELETE CASCADE,
    CONSTRAINT entity_record_versions_payload_object CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT entity_record_versions_payload_hash_length CHECK (length(payload_hash) = 64),
    CONSTRAINT entity_record_versions_observation_order CHECK (last_observed_at >= first_observed_at)
);

CREATE INDEX entity_record_versions_observed_idx
    ON entitysync.entity_record_versions (
        tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key, first_observed_at DESC);

CREATE TABLE entitysync.entity_relationships (
    tenant_id text NOT NULL,
    source_vendor_key text NOT NULL,
    source_connection_key text NOT NULL,
    source_entity_type_key text NOT NULL,
    source_entity_id_key text NOT NULL,
    target_vendor_key text NOT NULL,
    target_connection_key text NOT NULL,
    target_entity_type_key text NOT NULL,
    target_entity_id_key text NOT NULL,
    relationship_type_key text NOT NULL,
    relationship_type text NOT NULL,
    status text NOT NULL,
    match_type text NOT NULL,
    score integer NOT NULL,
    evidence jsonb NOT NULL DEFAULT '[]'::jsonb,
    first_observed_at timestamptz NOT NULL,
    last_observed_at timestamptz NOT NULL,
    confirmed_at timestamptz,
    last_plan_id text,
    PRIMARY KEY (
        tenant_id,
        source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key,
        target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key,
        relationship_type_key),
    FOREIGN KEY (
        tenant_id, source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key)
        REFERENCES entitysync.entity_records (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key),
    FOREIGN KEY (
        tenant_id, target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key)
        REFERENCES entitysync.entity_records (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key),
    CONSTRAINT entity_relationships_status CHECK (status IN ('Proposed', 'Confirmed', 'Removed')),
    CONSTRAINT entity_relationships_score CHECK (score BETWEEN 0 AND 100),
    CONSTRAINT entity_relationships_evidence_array CHECK (jsonb_typeof(evidence) = 'array'),
    CONSTRAINT entity_relationships_observation_order CHECK (last_observed_at >= first_observed_at)
);

CREATE INDEX entity_relationships_source_idx
    ON entitysync.entity_relationships (
        tenant_id, source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key,
        relationship_type_key, last_observed_at DESC);
CREATE INDEX entity_relationships_target_idx
    ON entitysync.entity_relationships (
        tenant_id, target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key,
        relationship_type_key, last_observed_at DESC);
