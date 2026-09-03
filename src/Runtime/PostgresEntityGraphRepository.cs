using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresEntityGraphRepository : IEntityGraphRepository
{
    private const string UpsertRecordsSql = """
        WITH input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows) AS row(
                tenant_id text, vendor_key text, connection_key text, entity_type_key text,
                entity_id_key text, vendor text, connection_id text, entity_type text,
                entity_id text, name text, normalized_name text, is_active boolean,
                payload jsonb, payload_hash text, observed_at timestamptz, plan_id text))
        INSERT INTO entitysync.entity_records (
            tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            vendor, connection_id, entity_type, entity_id, name, normalized_name, is_active,
            payload, payload_hash, first_observed_at, last_observed_at, last_plan_id)
        SELECT tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key,
            vendor, connection_id, entity_type, entity_id, name, normalized_name, is_active,
            payload, payload_hash, observed_at, observed_at, plan_id
        FROM input
        ON CONFLICT (tenant_id, vendor_key, connection_key, entity_type_key, entity_id_key)
        DO UPDATE SET
            vendor = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.vendor ELSE entity_records.vendor END,
            connection_id = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.connection_id ELSE entity_records.connection_id END,
            entity_type = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.entity_type ELSE entity_records.entity_type END,
            entity_id = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.entity_id ELSE entity_records.entity_id END,
            name = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.name ELSE entity_records.name END,
            normalized_name = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.normalized_name ELSE entity_records.normalized_name END,
            is_active = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.is_active ELSE entity_records.is_active END,
            payload = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.payload ELSE entity_records.payload END,
            payload_hash = CASE WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at THEN EXCLUDED.payload_hash ELSE entity_records.payload_hash END,
            first_observed_at = LEAST(entity_records.first_observed_at, EXCLUDED.first_observed_at),
            last_observed_at = GREATEST(entity_records.last_observed_at, EXCLUDED.last_observed_at),
            last_plan_id = CASE
                WHEN EXCLUDED.last_observed_at >= entity_records.last_observed_at
                    THEN COALESCE(EXCLUDED.last_plan_id, entity_records.last_plan_id)
                ELSE entity_records.last_plan_id
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
            source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key,
            target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key,
            relationship_type_key, relationship_type, status, match_type, score, evidence,
            first_observed_at, last_observed_at, confirmed_at, last_plan_id)
        SELECT tenant_id,
            source_vendor_key, source_connection_key, source_entity_type_key, source_entity_id_key,
            target_vendor_key, target_connection_key, target_entity_type_key, target_entity_id_key,
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
                entity_id_key = EntityGraphPersistence.Require(entity.Id, nameof(entity.Id), 512).ToLowerInvariant(),
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
                plan_id = planId
            };
        }).ToArray();
        if (rows.Length == 0) return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(rows, EntityGraphPersistence.JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteJsonBatchAsync(connection, transaction, UpsertRecordsSql, json, cancellationToken).ConfigureAwait(false);
        await ExecuteJsonBatchAsync(connection, transaction, UpsertVersionsSql, json, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        var tenantId = EntityGraphPersistence.Require(query.TenantId, nameof(query.TenantId), 256);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT vendor, connection_id, entity_type, entity_id, payload::text, payload_hash,
                first_observed_at, last_observed_at, last_plan_id
            FROM entitysync.entity_records
            WHERE tenant_id = @tenant_id
              AND (@vendor_key IS NULL OR vendor_key = @vendor_key)
              AND (@connection_key IS NULL OR connection_key = @connection_key)
              AND (@entity_type_key IS NULL OR entity_type_key = @entity_type_key)
              AND (@search IS NULL OR normalized_name LIKE '%' || @search || '%' OR entity_id_key = @search)
            ORDER BY last_observed_at DESC, normalized_name, entity_id_key
            LIMIT @count
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        AddOptionalText(command, "vendor_key", query.Vendor?.Trim().ToLowerInvariant());
        AddOptionalText(command, "connection_key", query.ConnectionId?.Trim().ToLowerInvariant());
        AddOptionalText(command, "entity_type_key", query.EntityType?.Trim().ToLowerInvariant());
        AddOptionalText(command, "search", query.Search?.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("count", query.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<EntityGraphRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = EntityGraphPersistence.Deserialize(reader.GetString(4));
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
