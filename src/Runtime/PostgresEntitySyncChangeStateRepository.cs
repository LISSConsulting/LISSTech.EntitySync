using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresEntitySyncChangeStateRepository : IEntitySyncChangeStateRepository
{
    private const string RoutePredicate = """
        tenant_id = @tenant_id
        AND route_scope = @route_scope
        AND source_vendor = @source_vendor
        AND source_connection_id = @source_connection_id
        AND source_entity_type = @source_entity_type
        AND target_vendor = @target_vendor
        AND target_connection_id = @target_connection_id
        AND target_entity_type = @target_entity_type
        """;

    private const string UpsertSql = """
        INSERT INTO entitysync.entity_change_state (
            tenant_id, route_scope, source_vendor, source_connection_id, source_entity_type,
            target_vendor, target_connection_id, target_entity_type, source_entity_key,
            source_entity_id, source_name, target_entity_id, hash_version, payload_hash, applied_at)
        VALUES (
            @tenant_id, @route_scope, @source_vendor, @source_connection_id, @source_entity_type,
            @target_vendor, @target_connection_id, @target_entity_type, @source_entity_key,
            @source_entity_id, @source_name, @target_entity_id, @hash_version, @payload_hash, @applied_at)
        ON CONFLICT (
            tenant_id, route_scope, source_vendor, source_connection_id, source_entity_type,
            target_vendor, target_connection_id, target_entity_type, source_entity_key)
        DO UPDATE SET
            source_name = EXCLUDED.source_name,
            target_entity_id = EXCLUDED.target_entity_id,
            hash_version = EXCLUDED.hash_version,
            payload_hash = EXCLUDED.payload_hash,
            applied_at = EXCLUDED.applied_at
        """;

    private readonly NpgsqlDataSource dataSource;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public PostgresEntitySyncChangeStateRepository(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
        EntitySyncChangeStateRoute route,
        IReadOnlyCollection<string> sourceEntityIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceEntityId in sourceEntityIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceKeys.Add(EntitySyncChangeStatePersistence.NormalizeSourceKey(sourceEntityId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (sourceKeys.Count == 0)
            return new Dictionary<string, EntitySyncChangeState>(StringComparer.OrdinalIgnoreCase);

        const string sql = """
            SELECT source_entity_id, source_name, target_entity_id, hash_version, payload_hash, applied_at
            FROM entitysync.entity_change_state
            WHERE source_entity_key = ANY(@source_keys) AND
            """ + "\n" + RoutePredicate;
        await using var command = dataSource.CreateCommand(sql);
        AddRoute(command, route);
        command.Parameters.AddWithValue("source_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, sourceKeys.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, EntitySyncChangeState>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = new EntitySyncChangeState(
                route with { },
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5));
            result[state.SourceEntityId] = state;
        }

        return result;
    }

    public async Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = CreateUpsertCommand(state);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand CreateUpsertCommand(EntitySyncChangeState state)
    {
        var validated = EntitySyncChangeStatePersistence.ValidateState(state);
        var command = dataSource.CreateCommand(UpsertSql);
        AddRoute(command, validated.State.Route);
        command.Parameters.AddWithValue("source_entity_key", validated.SourceEntityKey);
        command.Parameters.AddWithValue("source_entity_id", validated.State.SourceEntityId);
        command.Parameters.AddWithValue("source_name", validated.State.SourceName);
        command.Parameters.AddWithValue("target_entity_id", validated.State.TargetEntityId);
        command.Parameters.AddWithValue("hash_version", validated.State.HashVersion);
        command.Parameters.AddWithValue("payload_hash", validated.State.PayloadHash);
        command.Parameters.AddWithValue("applied_at", validated.State.AppliedAt);
        return command;
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

    private static void AddRoute(NpgsqlCommand command, EntitySyncChangeStateRoute route)
    {
        command.Parameters.AddWithValue("tenant_id", route.TenantId);
        command.Parameters.AddWithValue("route_scope", route.Scope);
        command.Parameters.AddWithValue("source_vendor", route.SourceVendor);
        command.Parameters.AddWithValue("source_connection_id", route.SourceConnectionId);
        command.Parameters.AddWithValue("source_entity_type", route.SourceEntityType);
        command.Parameters.AddWithValue("target_vendor", route.TargetVendor);
        command.Parameters.AddWithValue("target_connection_id", route.TargetConnectionId);
        command.Parameters.AddWithValue("target_entity_type", route.TargetEntityType);
    }
}
