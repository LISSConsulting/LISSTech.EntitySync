using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;


namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresEntityGraphRepository : IEntityGraphRepository
{
    // Conflict resolution ordering: prefer source_updated_at when both sides have a
    // non-null timestamp (late webhooks with older sources cannot overwrite newer
    // data), fall back to last_observed_at when source_updated_at is missing
    // (authoritative full snapshots stay authoritative).
    private const string UpsertRecordsSql = """
        WITH input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows) AS row(
                tenant_id text, vendor_key text, connection_key text, entity_type_key text,
                entity_id_key text, vendor text, connection_id text, entity_type text,
                entity_id text, name text, normalized_name text, is_active boolean,
                payload jsonb, payload_hash text, observed_at timestamptz, plan_id text,
                source_cursor text, source_updated_at timestamptz))
        INSERT INTO entitysync.entity_records (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            vendor, connection_id, entity_type, entity_id, name, normalized_name, is_active,
            payload, payload_hash, first_observed_at, last_observed_at, last_plan_id,
            source_cursor, source_updated_at)
        SELECT tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            vendor, connection_id, entity_type, entity_id, name, normalized_name, is_active,
            payload, payload_hash, observed_at, observed_at, plan_id,
            source_cursor, source_updated_at
        FROM input
        ON CONFLICT (tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key)
        DO UPDATE SET
            vendor = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.vendor ELSE entity_records.vendor END,
            connection_id = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.connection_id ELSE entity_records.connection_id END,
            entity_type = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.entity_type ELSE entity_records.entity_type END,
            entity_id = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.entity_id ELSE entity_records.entity_id END,
            name = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.name ELSE entity_records.name END,
            normalized_name = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.normalized_name ELSE entity_records.normalized_name END,
            is_active = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.is_active ELSE entity_records.is_active END,
            payload = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.payload ELSE entity_records.payload END,
            payload_hash = CASE WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                THEN EXCLUDED.payload_hash ELSE entity_records.payload_hash END,
            first_observed_at = LEAST(entity_records.first_observed_at, EXCLUDED.first_observed_at),
            last_observed_at = GREATEST(entity_records.last_observed_at, EXCLUDED.last_observed_at),
            last_plan_id = CASE
                WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                    THEN COALESCE(EXCLUDED.last_plan_id, entity_records.last_plan_id)
                ELSE entity_records.last_plan_id
            END,
            source_cursor = CASE
                WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                    THEN COALESCE(EXCLUDED.source_cursor, entity_records.source_cursor)
                ELSE entity_records.source_cursor
            END,
            source_updated_at = CASE
                WHEN COALESCE(EXCLUDED.source_updated_at, EXCLUDED.last_observed_at) >= COALESCE(entity_records.source_updated_at, entity_records.last_observed_at)
                    THEN COALESCE(EXCLUDED.source_updated_at, entity_records.source_updated_at)
                ELSE entity_records.source_updated_at
            END
        """;

    private const string UpsertVersionsSql = """
        WITH input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows) AS row(
                tenant_id text, vendor_key text, connection_key text, entity_type_key text,
                entity_id_key text, payload jsonb, payload_hash text,
                observed_at timestamptz, plan_id text))
        INSERT INTO entitysync.entity_record_versions (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            payload_hash, payload, first_observed_at, last_observed_at, last_plan_id)
        SELECT tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            payload_hash, payload, observed_at, observed_at, plan_id
        FROM input
        ON CONFLICT (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key, payload_hash)
        DO UPDATE SET
            first_observed_at = LEAST(entity_record_versions.first_observed_at, EXCLUDED.first_observed_at),
            last_observed_at = GREATEST(entity_record_versions.last_observed_at, EXCLUDED.last_observed_at),
            last_plan_id = COALESCE(EXCLUDED.last_plan_id, entity_record_versions.last_plan_id)
        """;

    private const string UpsertRelationshipsSql = """
        WITH input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows) AS row(
                tenant_id text,
                source_vendor_key text, source_connection_key text,
                source_entity_type_key text, source_entity_id_key text,
                target_vendor_key text, target_connection_key text,
                target_entity_type_key text, target_entity_id_key text,
                relationship_type_key text, relationship_type text, status text,
                match_type text, score integer, evidence jsonb,
                observed_at timestamptz, confirmed_at timestamptz, plan_id text))
        INSERT INTO entitysync.entity_relationships (
            tenant_id,
            source_vendor_key, source_connection_key,
            source_entity_type_key, source_entity_id_key,
            target_vendor_key, target_connection_key,
            target_entity_type_key, target_entity_id_key,
            relationship_type_key, relationship_type, status, match_type, score, evidence,
            first_observed_at, last_observed_at, confirmed_at, last_plan_id)
        SELECT tenant_id,
            source_vendor_key, source_connection_key,
            source_entity_type_key, source_entity_id_key,
            target_vendor_key, target_connection_key,
            target_entity_type_key, target_entity_id_key,
            relationship_type_key, relationship_type, status, match_type, score, evidence,
            observed_at, observed_at, confirmed_at, plan_id
        FROM input
        ON CONFLICT (
            tenant_id,
            source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key,
            target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key,
            relationship_type_key)
        DO UPDATE SET
            relationship_type = CASE
                WHEN EXCLUDED.last_observed_at >= entity_relationships.last_observed_at
                    THEN EXCLUDED.relationship_type
                ELSE entity_relationships.relationship_type
            END,
            status = CASE
                WHEN EXCLUDED.last_observed_at < entity_relationships.last_observed_at
                    THEN entity_relationships.status
                WHEN entity_relationships.status = 'Confirmed' AND EXCLUDED.status = 'Proposed'
                    THEN entity_relationships.status
                ELSE EXCLUDED.status
            END,
            match_type = CASE
                WHEN EXCLUDED.last_observed_at < entity_relationships.last_observed_at
                    THEN entity_relationships.match_type
                WHEN entity_relationships.status = 'Confirmed' AND EXCLUDED.status = 'Proposed'
                    THEN entity_relationships.match_type
                ELSE EXCLUDED.match_type
            END,
            score = CASE
                WHEN EXCLUDED.last_observed_at < entity_relationships.last_observed_at
                    THEN entity_relationships.score
                WHEN entity_relationships.status = 'Confirmed' AND EXCLUDED.status = 'Proposed'
                    THEN entity_relationships.score
                ELSE EXCLUDED.score
            END,
            evidence = CASE
                WHEN EXCLUDED.last_observed_at < entity_relationships.last_observed_at
                    THEN entity_relationships.evidence
                WHEN entity_relationships.status = 'Confirmed' AND EXCLUDED.status = 'Proposed'
                    THEN entity_relationships.evidence
                ELSE EXCLUDED.evidence
            END,
            first_observed_at = LEAST(entity_relationships.first_observed_at, EXCLUDED.first_observed_at),
            last_observed_at = GREATEST(entity_relationships.last_observed_at, EXCLUDED.last_observed_at),
            confirmed_at = CASE
                WHEN EXCLUDED.last_observed_at >= entity_relationships.last_observed_at
                    THEN COALESCE(entity_relationships.confirmed_at, EXCLUDED.confirmed_at)
                ELSE entity_relationships.confirmed_at
            END,
            last_plan_id = CASE
                WHEN EXCLUDED.last_observed_at >= entity_relationships.last_observed_at
                    THEN COALESCE(EXCLUDED.last_plan_id, entity_relationships.last_plan_id)
                ELSE entity_relationships.last_plan_id
            END
        """;

    private readonly NpgsqlDataSource dataSource;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public PostgresEntityGraphRepository(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task ObserveEntitiesAsync(EntityGraphObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = EntityGraphPersistence.ValidateScope(observation.Scope);
        var planId = EntityGraphPersistence.Optional(observation.PlanId, 128);
        var rows = observation.Entities.Select(entity =>
        {
            var payload = EntityGraphPersistence.Serialize(entity);
            return new
            {
                tenant_id = scope.TenantId,
                vendor_key = scope.Vendor.ToLowerInvariant(),
                connection_key = scope.ConnectionId.ToLowerInvariant(),
                entity_type_key = scope.EntityType.ToLowerInvariant(),
                entity_id_key = EntityGraphPersistence.Require(entity.Id, nameof(entity.Id), 512)
                    .ToLowerInvariant(),
                vendor = scope.Vendor,
                connection_id = scope.ConnectionId,
                entity_type = scope.EntityType,
                entity_id = entity.Id.Trim(),
                name = entity.Name?.Trim() ?? string.Empty,
                normalized_name = entity.NormalizedName,
                is_active = entity.IsActive,
                payload = JsonSerializer.Deserialize<JsonElement>(payload),
                payload_hash = EntityGraphPersistence.Hash(payload),
                observed_at = observation.ObservedAt,
                plan_id = planId,
                source_cursor = observation.Cursor,
                source_updated_at = observation.SourceUpdatedAt
            };
        }).ToArray();
        if (rows.Length == 0) return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(rows, EntityGraphPersistence.JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteJsonBatchAsync(connection, transaction, UpsertRecordsSql, json, cancellationToken).ConfigureAwait(false);
            await ExecuteJsonBatchAsync(connection, transaction, UpsertVersionsSql, json, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ObserveRelationshipsAsync(
        IReadOnlyCollection<EntityGraphRelationshipObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = observations.Select(EntityGraphPersistence.ValidateRelationship).Select(relationship => new
        {
            tenant_id = relationship.Source.TenantId,
            source_vendor_key = relationship.Source.Vendor.ToLowerInvariant(),
            source_connection_key = relationship.Source.ConnectionId.ToLowerInvariant(),
            source_entity_type_key = relationship.Source.EntityType.ToLowerInvariant(),
            source_entity_id_key = relationship.Source.EntityId.ToLowerInvariant(),
            target_vendor_key = relationship.Target.Vendor.ToLowerInvariant(),
            target_connection_key = relationship.Target.ConnectionId.ToLowerInvariant(),
            target_entity_type_key = relationship.Target.EntityType.ToLowerInvariant(),
            target_entity_id_key = relationship.Target.EntityId.ToLowerInvariant(),
            relationship_type_key = relationship.RelationshipType.ToLowerInvariant(),
            relationship_type = relationship.RelationshipType,
            status = relationship.Status,
            match_type = relationship.MatchType,
            score = relationship.Score,
            evidence = relationship.Evidence,
            observed_at = relationship.ObservedAt,
            confirmed_at = relationship.Status == EntityGraphRelationshipStatuses.Confirmed
                ? relationship.ObservedAt
                : (DateTimeOffset?)null,
            plan_id = relationship.PlanId
        }).ToArray();
        if (rows.Length == 0) return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(rows, EntityGraphPersistence.JsonOptions);
        await using var command = dataSource.CreateCommand(UpsertRelationshipsSql);
        command.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, json);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EntityGraphRecord>> QueryEntitiesAsync(
        EntityGraphQuery query,
        CancellationToken cancellationToken)
    {
        ValidateCount(query.Count);
        if (query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query.Offset));
        var tenantId = EntityGraphPersistence.Require(query.TenantId, nameof(query.TenantId), 256);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT vendor, connection_id, entity_type, entity_id, payload::text, payload_hash,
                first_observed_at, last_observed_at, last_plan_id, is_active
            FROM entitysync.entity_records
            WHERE tenant_id = @tenant_id
              AND (@vendor_key IS NULL OR vendor_key = @vendor_key)
              AND (@connection_key IS NULL OR connection_key = @connection_key)
              AND (@search IS NULL OR normalized_name LIKE '%' || @search || '%' OR entity_id_key = @search)
              -- Unknown is_active (NULL) is treated as visible per public semantics.
              AND (@include_inactive OR is_active IS DISTINCT FROM false)
            ORDER BY last_observed_at DESC, normalized_name, entity_id_key
            LIMIT @count OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        AddOptionalText(command, "vendor_key", query.Vendor?.Trim().ToLowerInvariant());
        AddOptionalText(command, "connection_key", query.ConnectionId?.Trim().ToLowerInvariant());
        AddOptionalText(command, "entity_type_key", query.EntityType?.Trim().ToLowerInvariant());
        AddOptionalText(command, "search", query.Search?.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("include_inactive", query.IncludeInactive);
        command.Parameters.AddWithValue("count", query.Count);
        command.Parameters.AddWithValue("offset", query.Offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<EntityGraphRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = EntityGraphPersistence.Deserialize(reader.GetString(4));
            // NULL is_active = unknown authority; treat as visible. Only overlay the
            // tombstone when the column is explicitly FALSE.
            var isActiveColumn = reader.IsDBNull(9) ? (bool?)null : reader.GetBoolean(9);
            if (isActiveColumn == false)
            {
                entity.IsActive = false;
                entity.IsDeleted = true;
            }
            records.Add(new EntityGraphRecord(
                new EntityGraphNodeKey(tenantId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                entity,
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return records;
    }

    public async Task<IReadOnlyList<EntityGraphRelationship>> QueryRelationshipsAsync(
        EntityGraphRelationshipQuery query,
        CancellationToken cancellationToken)
    {
        ValidateCount(query.Count);
        var node = EntityGraphPersistence.ValidateKey(query.Node);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT
                source.vendor, source.connection_id, source.entity_type, source.entity_id,
                target.vendor, target.connection_id, target.entity_type, target.entity_id,
                relationship.relationship_type, relationship.status, relationship.match_type,
                relationship.score, relationship.evidence::text,
                relationship.first_observed_at, relationship.last_observed_at,
                relationship.confirmed_at, relationship.last_plan_id
            FROM entitysync.entity_relationships relationship
            JOIN entitysync.entity_records source ON
                source.tenant_id = relationship.tenant_id
                AND source.vendor_key = relationship.source_vendor_key
                AND source.connection_key = relationship.source_connection_key
                AND source.entity_type_key = relationship.source_entity_type_key
                AND source.entity_id_key = relationship.source_entity_id_key
            JOIN entitysync.entity_records target ON
                target.tenant_id = relationship.tenant_id
                AND target.vendor_key = relationship.target_vendor_key
                AND target.connection_key = relationship.target_connection_key
                AND target.entity_type_key = relationship.target_entity_type_key
                AND target.entity_id_key = relationship.target_entity_id_key
            WHERE relationship.tenant_id = @tenant_id
              AND (@relationship_type_key IS NULL OR relationship.relationship_type_key = @relationship_type_key)
              AND ((
                    relationship.source_vendor_key = @vendor_key
                AND relationship.source_connection_key = @connection_key
                AND relationship.source_entity_type_key = @entity_type_key
                AND relationship.source_entity_id_key = @entity_id_key)
                OR (
                    relationship.target_vendor_key = @vendor_key
                AND relationship.target_connection_key = @connection_key
                AND relationship.target_entity_type_key = @entity_type_key
                AND relationship.target_entity_id_key = @entity_id_key))
            ORDER BY relationship.last_observed_at DESC
            LIMIT @count
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", node.TenantId);
        command.Parameters.AddWithValue("vendor_key", node.Vendor.ToLowerInvariant());
        command.Parameters.AddWithValue("connection_key", node.ConnectionId.ToLowerInvariant());
        command.Parameters.AddWithValue("entity_type_key", node.EntityType.ToLowerInvariant());
        command.Parameters.AddWithValue("entity_id_key", node.EntityId.ToLowerInvariant());
        AddOptionalText(command, "relationship_type_key", query.RelationshipType?.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("count", query.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var relationships = new List<EntityGraphRelationship>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            relationships.Add(new EntityGraphRelationship(
                new EntityGraphNodeKey(node.TenantId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                new EntityGraphNodeKey(node.TenantId, reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt32(11),
                JsonSerializer.Deserialize<string[]>(reader.GetString(12), EntityGraphPersistence.JsonOptions) ?? [],
                reader.GetFieldValue<DateTimeOffset>(13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
        }
        return relationships;
    }

    public async Task<EntityRefreshSnapshotResult> ReplaceAuthoritativeSnapshotAsync(
        EntityGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = EntityGraphPersistence.ValidateScope(snapshot.Scope);
        if (snapshot.ConnectionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                "Connection generation must be positive.");
        if (snapshot.SnapshotStartedAt > snapshot.ObservedAt)
            throw new ArgumentException(
                "Snapshot started-at must precede or equal observed-at.",
                nameof(snapshot));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var rows = snapshot.Entities.Select(entity =>
            {
                var payload = EntityGraphPersistence.Serialize(entity);
                return new
                {
                    tenant_id = scope.TenantId,
                    vendor_key = scope.Vendor.ToLowerInvariant(),
                    connection_key = scope.ConnectionId.ToLowerInvariant(),
                    entity_type_key = scope.EntityType.ToLowerInvariant(),
                    entity_id_key = EntityGraphPersistence.Require(entity.Id, nameof(entity.Id), 512)
                        .ToLowerInvariant(),
                    vendor = scope.Vendor,
                    connection_id = scope.ConnectionId,
                    entity_type = scope.EntityType,
                    entity_id = entity.Id.Trim(),
                    name = entity.Name?.Trim() ?? string.Empty,
                    normalized_name = entity.NormalizedName,
                    is_active = entity.IsActive,
                    payload = JsonSerializer.Deserialize<JsonElement>(payload),
                    payload_hash = EntityGraphPersistence.Hash(payload),
                    observed_at = snapshot.ObservedAt,
                    plan_id = EntityGraphPersistence.Optional(snapshot.PlanId, 128),
                    source_cursor = snapshot.Cursor,
                    source_updated_at = snapshot.SourceUpdatedAt
                };
            }).ToArray();

            if (rows.Length > 0)
            {
                var json = JsonSerializer.Serialize(rows, EntityGraphPersistence.JsonOptions);
                await ExecuteJsonBatchAsync(connection, transaction, UpsertRecordsSql, json, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteJsonBatchAsync(connection, transaction, UpsertVersionsSql, json, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Authoritative snapshot seal: do NOT mutate payload or payload_hash. Only
            // flip the authoritative is_active column and the observation timestamp.
            const string snapshotSealSql = """
                WITH seen AS (
                    SELECT LOWER(@entity_type) AS entity_type_key,
                           LOWER(@vendor) AS vendor_key,
                           LOWER(@connection_id) AS connection_key,
                           unnest(@seen_keys::text[]) AS entity_id_key
                ),
                sealed AS (
                    UPDATE entitysync.entity_records r
                    SET is_active = false,
                        last_observed_at = @observed_at
                    WHERE r.tenant_id = @tenant_id
                      AND r.vendor_key = LOWER(@vendor)
                      AND r.connection_key = LOWER(@connection_id)
                      AND r.entity_type_key = LOWER(@entity_type)
                      AND r.first_observed_at < @snapshot_started_at
                      AND r.last_observed_at < @snapshot_started_at
                      AND NOT EXISTS (
                          SELECT 1 FROM seen
                          WHERE seen.entity_type_key = r.entity_type_key
                            AND seen.vendor_key = r.vendor_key
                            AND seen.connection_key = r.connection_key
                            AND seen.entity_id_key = r.entity_id_key)
                    RETURNING r.entity_id_key
                ),
                preserved AS (
                    SELECT count(*)::bigint AS preserved_count
                      FROM entitysync.entity_records r
                     WHERE r.tenant_id = @tenant_id
                       AND r.vendor_key = LOWER(@vendor)
                       AND r.connection_key = LOWER(@connection_id)
                       AND r.entity_type_key = LOWER(@entity_type)
                       AND r.last_observed_at >= @snapshot_started_at
                       AND NOT EXISTS (
                           SELECT 1 FROM seen
                           WHERE seen.entity_type_key = r.entity_type_key
                             AND seen.vendor_key = r.vendor_key
                             AND seen.connection_key = r.connection_key
                             AND seen.entity_id_key = r.entity_id_key)
                )
                SELECT
                    (SELECT count(*) FROM sealed) AS tombstoned,
                    (SELECT preserved_count FROM preserved) AS preserved
                """;
            await using (var sealCommand = new NpgsqlCommand(snapshotSealSql, connection, transaction))
            {
                sealCommand.Parameters.AddWithValue("tenant_id", scope.TenantId);
                sealCommand.Parameters.AddWithValue("vendor", scope.Vendor);
                sealCommand.Parameters.AddWithValue("connection_id", scope.ConnectionId);
                sealCommand.Parameters.AddWithValue("entity_type", scope.EntityType);
                sealCommand.Parameters.AddWithValue("snapshot_started_at", snapshot.SnapshotStartedAt);
                sealCommand.Parameters.AddWithValue("observed_at", snapshot.ObservedAt);
                var seenIds = rows.Select(r => (object)r.entity_id_key).ToArray();
                sealCommand.Parameters.Add(new NpgsqlParameter
                {
                    ParameterName = "seen_keys",
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                    Value = seenIds.Length == 0 ? Array.Empty<string>() : seenIds.Select(v => (string)v).ToArray()
                });
                await using var reader = await sealCommand.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                long tombstoned = 0;
                long preserved = 0;
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tombstoned = reader.GetInt64(0);
                    preserved = reader.GetInt64(1);
                }
                await reader.CloseAsync().ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new EntityRefreshSnapshotResult(
                    scope,
                    snapshot.SnapshotStartedAt,
                    snapshot.ObservedAt,
                    rows.Length,
                    tombstoned,
                    preserved,
                    rows.Length);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EntityAtomicEventOutcome> ApplyAtomicEventAsync(
        EntityGraphScope scope,
        EntityAtomicEvent atomicEvent,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validatedScope = EntityGraphPersistence.ValidateScope(scope);
        if (atomicEvent.EventId == Guid.Empty)
            throw new ArgumentException("Atomic event ID is required.", nameof(atomicEvent));
        if (atomicEvent.Entity is null || string.IsNullOrWhiteSpace(atomicEvent.Entity.Id))
            throw new ArgumentException(
                "Atomic events require an entity payload with non-blank id.",
                nameof(atomicEvent));
        if (connectionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration),
                "Connection generation must be positive.");
        if (string.IsNullOrWhiteSpace(atomicEvent.EntityType))
            throw new ArgumentException("Atomic event entity type is required.",
                nameof(atomicEvent));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Enforce connection generation atomically: a stale writer cannot
            // overwrite a rotated connection's records. The FOR UPDATE row lock on
            // connection_definitions is held for the rest of this transaction so a
            // concurrent rotate cannot interleave between the check and the write.
            const string generationGuardSql = """
                SELECT generation
                  FROM entitysync.connection_definitions
                 WHERE tenant_id = @tenant_id
                   AND connection_id = @connection_id
                 FOR UPDATE
                """;
            long currentGeneration;
            await using (var guard = new NpgsqlCommand(generationGuardSql, connection, transaction))
            {
                guard.Parameters.AddWithValue("tenant_id", validatedScope.TenantId);
                guard.Parameters.AddWithValue("connection_id", validatedScope.ConnectionId);
                var observed = await guard.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (observed is null || observed is DBNull)
                    throw new ConnectionNotFoundException(validatedScope.TenantId, validatedScope.ConnectionId);
                currentGeneration = (long)observed;
            }
            if (currentGeneration != connectionGeneration)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ConnectionGenerationConflictException(
                    validatedScope.ConnectionId, connectionGeneration);
            }

            const string insertReceiptSql = """
                INSERT INTO entitysync.entity_event_receipts (
                    tenant_id, event_id, connection_id, entity_type, entity_id,
                    operation, payload_sha256, source_cursor, source_updated_at,
                    received_at, applied_at)
                VALUES (
                    @tenant_id, @event_id, @connection_id, @entity_type, @entity_id,
                    @operation, @payload_sha256, @source_cursor, @source_updated_at,
                    @received_at, @applied_at)
                ON CONFLICT (tenant_id, event_id) DO NOTHING
                RETURNING event_id
                """;
            var entityId = atomicEvent.Entity.Id;
            string payload;
            if (atomicEvent.Operation == EntityAtomicOperation.Upsert)
                payload = EntityGraphPersistence.Serialize(atomicEvent.Entity);
            else
                payload = "[]";
            var sha = EntityGraphPersistence.Hash(payload);
            var receivedAt = DateTimeOffset.UtcNow;
            await using (var insertReceipt = new NpgsqlCommand(insertReceiptSql, connection, transaction))
            {
                insertReceipt.Parameters.AddWithValue("tenant_id", validatedScope.TenantId);
                insertReceipt.Parameters.AddWithValue("event_id", atomicEvent.EventId);
                insertReceipt.Parameters.AddWithValue("connection_id", validatedScope.ConnectionId);
                insertReceipt.Parameters.AddWithValue("entity_type", atomicEvent.EntityType);
                insertReceipt.Parameters.AddWithValue("entity_id", entityId);
                insertReceipt.Parameters.AddWithValue("operation",
                    atomicEvent.Operation.ToString());
                insertReceipt.Parameters.AddWithValue("payload_sha256", sha);
                AddOptionalText(insertReceipt, "source_cursor", atomicEvent.SourceCursor);
                insertReceipt.Parameters.Add(new NpgsqlParameter("source_updated_at",
                    NpgsqlDbType.TimestampTz)
                {
                    Value = atomicEvent.SourceUpdatedAt ?? (object)DBNull.Value
                });
                insertReceipt.Parameters.AddWithValue("received_at", receivedAt);
                insertReceipt.Parameters.AddWithValue("applied_at", receivedAt);
                var inserted = await insertReceipt.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (inserted is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new EntityAtomicEventOutcome(
                        EntityAtomicEventOutcomeKind.Duplicate, null, receivedAt);
                }
            }

            EntityAtomicEventOutcome outcome;
            if (atomicEvent.Operation == EntityAtomicOperation.Upsert)
            {
                var entity = atomicEvent.Entity!;
                var rows = new[]
                {
                    new
                    {
                        tenant_id = validatedScope.TenantId,
                        vendor_key = validatedScope.Vendor.ToLowerInvariant(),
                        connection_key = validatedScope.ConnectionId.ToLowerInvariant(),
                        entity_type_key = atomicEvent.EntityType.ToLowerInvariant(),
                        entity_id_key = EntityGraphPersistence.Require(entity.Id, nameof(entity.Id), 512)
                            .ToLowerInvariant(),
                        vendor = validatedScope.Vendor,
                        connection_id = validatedScope.ConnectionId,
                        entity_type = atomicEvent.EntityType,
                        entity_id = entity.Id.Trim(),
                        name = entity.Name?.Trim() ?? string.Empty,
                        normalized_name = entity.NormalizedName,
                        is_active = entity.IsActive,
                        payload = JsonSerializer.Deserialize<JsonElement>(payload),
                        payload_hash = sha,
                        observed_at = receivedAt,
                        plan_id = (string?)null,
                        source_cursor = atomicEvent.SourceCursor,
                        source_updated_at = atomicEvent.SourceUpdatedAt
                    }
                };
                var json = JsonSerializer.Serialize(rows, EntityGraphPersistence.JsonOptions);
                await ExecuteJsonBatchAsync(connection, transaction, UpsertRecordsSql, json, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteJsonBatchAsync(connection, transaction, UpsertVersionsSql, json, cancellationToken)
                    .ConfigureAwait(false);
                var record = new EntityGraphRecord(
                    new EntityGraphNodeKey(validatedScope.TenantId, validatedScope.Vendor,
                        validatedScope.ConnectionId, atomicEvent.EntityType, entityId),
                    entity,
                    sha,
                    receivedAt,
                    receivedAt,
                    null);
                outcome = new EntityAtomicEventOutcome(
                    EntityAtomicEventOutcomeKind.Applied, record, receivedAt);
            }
            else
            {
                // Atomic delete: flip the authoritative is_active column; do NOT mutate
                // payload or payload_hash. Readers overlay IsActive from the column.
                const string tombstoneSql = """
                    UPDATE entitysync.entity_records
                       SET is_active = false,
                           last_observed_at = @observed_at
                     WHERE tenant_id = @tenant_id
                       AND vendor_key = LOWER(@vendor)
                       AND connection_key = LOWER(@connection_id)
                       AND entity_type_key = LOWER(@entity_type)
                       AND entity_id_key = LOWER(@entity_id)
                    RETURNING entity_id_key
                    """;
                await using var tombstone = new NpgsqlCommand(tombstoneSql, connection, transaction);
                tombstone.Parameters.AddWithValue("tenant_id", validatedScope.TenantId);
                tombstone.Parameters.AddWithValue("vendor", validatedScope.Vendor);
                tombstone.Parameters.AddWithValue("connection_id", validatedScope.ConnectionId);
                tombstone.Parameters.AddWithValue("entity_type", atomicEvent.EntityType);
                tombstone.Parameters.AddWithValue("entity_id", entityId);
                tombstone.Parameters.AddWithValue("observed_at", receivedAt);
                var deleted = await tombstone.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (deleted is null)
                {
                    outcome = new EntityAtomicEventOutcome(
                        EntityAtomicEventOutcomeKind.NotFound, null, receivedAt);
                }
                else
                {
                    var tombstoneEntity = atomicEvent.Entity ?? new ExternalEntity
                    {
                        Vendor = validatedScope.Vendor,
                        EntityType = atomicEvent.EntityType,
                        Id = entityId,
                        Name = string.Empty,
                        IsActive = false
                    };
                    tombstoneEntity.IsActive = false;
                    tombstoneEntity.IsDeleted = true;
                    var record = new EntityGraphRecord(
                        new EntityGraphNodeKey(validatedScope.TenantId, validatedScope.Vendor,
                            validatedScope.ConnectionId, atomicEvent.EntityType, entityId),
                        tombstoneEntity,
                        sha,
                        receivedAt,
                        receivedAt,
                        atomicEvent.EventId.ToString());
                    outcome = new EntityAtomicEventOutcome(
                        EntityAtomicEventOutcomeKind.Applied, record, receivedAt);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EntityAtomicEventOutcome?> TryGetAtomicEventReceiptAsync(
        string tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT rcp.connection_id, rcp.entity_type, rcp.entity_id, rcp.operation,
                   rcp.applied_at,
                   r.vendor, r.connection_id, r.entity_type, r.entity_id,
                   r.payload::text, r.payload_hash, r.first_observed_at,
                   r.last_observed_at, r.last_plan_id, r.is_active
              FROM entitysync.entity_event_receipts rcp
              LEFT JOIN entitysync.entity_records r
                ON r.tenant_id = rcp.tenant_id
               AND r.connection_key = LOWER(rcp.connection_id)
               AND r.entity_type_key = LOWER(rcp.entity_type)
               AND r.entity_id_key = LOWER(rcp.entity_id)
             WHERE rcp.tenant_id = @tenant_id AND rcp.event_id = @event_id
             LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var connectionId = reader.GetString(0);
        var entityType = reader.GetString(1);
        var entityId = reader.GetString(2);
        var appliedAt = reader.GetFieldValue<DateTimeOffset>(4);
        var vendor = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        if (reader.IsDBNull(9))
        {
            return new EntityAtomicEventOutcome(
                EntityAtomicEventOutcomeKind.Duplicate,
                null,
                appliedAt);
        }
        var payload = reader.GetString(9);
        var hash = reader.GetString(10);
        var first = reader.GetFieldValue<DateTimeOffset>(11);
        var last = reader.GetFieldValue<DateTimeOffset>(12);
        var planId = reader.IsDBNull(13) ? null : reader.GetString(13);
        var entity = EntityGraphPersistence.Deserialize(payload);
        if (!reader.GetBoolean(14))
        {
            entity.IsActive = false;
            entity.IsDeleted = true;
        }
        var record = new EntityGraphRecord(
            new EntityGraphNodeKey(tenantId, vendor, connectionId, entityType, entityId),
            entity,
            hash,
            first,
            last,
            planId);
        return new EntityAtomicEventOutcome(
            EntityAtomicEventOutcomeKind.Duplicate, record, last);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await EntitySyncDatabaseMigrator.ApplyAsync(dataSource, cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    private static async Task ExecuteJsonBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string json,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, json);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddOptionalText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });
    }

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 1000.");
    }
}
