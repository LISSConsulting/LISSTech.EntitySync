ALTER TABLE entitysync.sync_plan_items
    ADD COLUMN resolved_target_parent jsonb;

ALTER TABLE entitysync.sync_operation_items
    ADD COLUMN resolved_target_parent jsonb;

ALTER TABLE entitysync.sync_plan_items
    ADD CONSTRAINT sync_plan_items_resolved_target_parent_check
    CHECK (
        resolved_target_parent IS NULL
        OR (
            lower(target_vendor) = 'orchestramsp'
            AND lower(action) = 'create'
            AND lower(target_entity_type) IN ('site', 'address')
            AND jsonb_typeof(resolved_target_parent) = 'object'
            AND resolved_target_parent ?& ARRAY[
                'ClientId', 'SiteId', 'ParentEntityType',
                'SourcePlatformInstanceId', 'MatchedLinkExternalId',
                'MatchedLinkStatus', 'MatchedLinkToken', 'ObservedOwnerVersion']
            AND (resolved_target_parent ->> 'ClientId')
                ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND (
                resolved_target_parent -> 'SiteId' = 'null'::jsonb
                OR (resolved_target_parent ->> 'SiteId')
                    ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            )
            AND lower(resolved_target_parent ->> 'ParentEntityType')
                IN ('client', 'site')
            AND btrim(resolved_target_parent ->> 'SourcePlatformInstanceId') <> ''
            AND btrim(resolved_target_parent ->> 'MatchedLinkExternalId') <> ''
            AND lower(resolved_target_parent ->> 'MatchedLinkStatus') = 'active'
            AND (resolved_target_parent ->> 'MatchedLinkToken') ~ '^[0-9a-f]{64}$'
            AND jsonb_typeof(
                resolved_target_parent -> 'ObservedOwnerVersion') = 'number'
            AND (resolved_target_parent ->> 'ObservedOwnerVersion')::bigint > 0
        )
    );

ALTER TABLE entitysync.sync_operation_items
    ADD CONSTRAINT sync_operation_items_resolved_target_parent_check
    CHECK (
        resolved_target_parent IS NULL
        OR (
            lower(target_vendor) = 'orchestramsp'
            AND lower(action) = 'create'
            AND lower(target_entity_type) IN ('site', 'address')
            AND jsonb_typeof(resolved_target_parent) = 'object'
            AND resolved_target_parent ?& ARRAY[
                'ClientId', 'SiteId', 'ParentEntityType',
                'SourcePlatformInstanceId', 'MatchedLinkExternalId',
                'MatchedLinkStatus', 'MatchedLinkToken', 'ObservedOwnerVersion']
            AND (resolved_target_parent ->> 'ClientId')
                ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND (
                resolved_target_parent -> 'SiteId' = 'null'::jsonb
                OR (resolved_target_parent ->> 'SiteId')
                    ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            )
            AND lower(resolved_target_parent ->> 'ParentEntityType')
                IN ('client', 'site')
            AND btrim(resolved_target_parent ->> 'SourcePlatformInstanceId') <> ''
            AND btrim(resolved_target_parent ->> 'MatchedLinkExternalId') <> ''
            AND lower(resolved_target_parent ->> 'MatchedLinkStatus') = 'active'
            AND (resolved_target_parent ->> 'MatchedLinkToken') ~ '^[0-9a-f]{64}$'
            AND jsonb_typeof(
                resolved_target_parent -> 'ObservedOwnerVersion') = 'number'
            AND (resolved_target_parent ->> 'ObservedOwnerVersion')::bigint > 0
        )
    );
