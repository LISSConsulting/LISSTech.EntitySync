using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresSyncPolicyRepository(NpgsqlDataSource dataSource) : ISyncPolicyRepository
{
    public async Task InsertAsync(
        string tenantId,
        EntitySyncPolicy policy,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, policy.TenantId, nameof(policy));
        const string sql = """
            INSERT INTO entitysync.sync_policies (
                tenant_id, policy_id, version, name, route_scope, definition,
                definition_sha256, enabled, created_at, created_by)
            VALUES (
                @tenant_id, @policy_id, @version, @name, @route_scope, @definition,
                @definition_sha256, @enabled, @created_at, @created_by)
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockPolicyIdentityAsync(
            connection, transaction, tenantId, policy.PolicyId, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPolicy(command, policy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryInsertValidatedAsync(
        string tenantId,
        EntitySyncPolicy policy,
        string sourceConnectionId,
        long sourceGeneration,
        string targetConnectionId,
        long targetGeneration,
        CancellationToken cancellationToken) =>
        TryInsertValidatedCoreAsync(
            tenantId, policy, sourceConnectionId, sourceGeneration,
            targetConnectionId, targetGeneration, null, cancellationToken);

    public Task<bool> TryInsertValidatedWithTokenAsync(
        string tenantId,
        EntitySyncPolicy policy,
        string sourceConnectionId,
        long sourceGeneration,
        string targetConnectionId,
        long targetGeneration,
        string idempotencyToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyToken);
        return TryInsertValidatedCoreAsync(
            tenantId, policy, sourceConnectionId, sourceGeneration,
            targetConnectionId, targetGeneration, idempotencyToken.Trim(),
            cancellationToken);
    }

    private async Task<bool> TryInsertValidatedCoreAsync(
        string tenantId,
        EntitySyncPolicy policy,
        string sourceConnectionId,
        long sourceGeneration,
        string targetConnectionId,
        long targetGeneration,
        string? idempotencyToken,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(
            tenantId,
            policy.TenantId,
            nameof(policy));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockPolicyIdentityAsync(
            connection, transaction, tenantId, policy.PolicyId, cancellationToken)
            .ConfigureAwait(false);
        const string lockSql = """
            SELECT connection_id, generation, enabled
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id
              AND connection_id IN (@source_connection_id, @target_connection_id)
            ORDER BY connection_id
            FOR SHARE
            """;
        await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
        PostgresControlPersistence.Add(
            lockCommand,
            "tenant_id",
            NpgsqlDbType.Text,
            tenantId);
        PostgresControlPersistence.Add(
            lockCommand,
            "source_connection_id",
            NpgsqlDbType.Text,
            sourceConnectionId);
        PostgresControlPersistence.Add(
            lockCommand,
            "target_connection_id",
            NpgsqlDbType.Text,
            targetConnectionId);
        var generations = new Dictionary<string, (long Generation, bool Enabled)>(
            StringComparer.Ordinal);
        await using (var reader = await lockCommand.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                generations.Add(
                    reader.GetString(0),
                    (reader.GetInt64(1), reader.GetBoolean(2)));
        }
        if (!Matches(sourceConnectionId, sourceGeneration)
            || !Matches(targetConnectionId, targetGeneration))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        const string insertSql = """
            INSERT INTO entitysync.sync_policies (
                tenant_id, policy_id, version, name, route_scope, definition,
                definition_sha256, enabled, created_at, created_by, idempotency_token)
            VALUES (
                @tenant_id, @policy_id, @version, @name, @route_scope, @definition,
                @definition_sha256, @enabled, @created_at, @created_by, @idempotency_token)
            """;
        await using var insertCommand = new NpgsqlCommand(
            insertSql,
            connection,
            transaction);
        AddPolicy(insertCommand, policy);
        PostgresControlPersistence.Add(
            insertCommand, "idempotency_token", NpgsqlDbType.Char, idempotencyToken);
        try
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool Matches(string connectionId, long generation) =>
            generations.TryGetValue(connectionId, out var current)
            && current.Enabled
            && current.Generation == generation;
    }

    public async Task<EntitySyncPolicy?> GetByIdempotencyTokenAsync(
        string tenantId,
        Guid policyId,
        string idempotencyToken,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, policy_id, version, name, route_scope, definition::text,
                   definition_sha256, enabled, created_at, created_by
            FROM entitysync.sync_policies
            WHERE tenant_id = @tenant_id
              AND policy_id = @policy_id
              AND idempotency_token = @idempotency_token
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, policyId);
        PostgresControlPersistence.Add(
            command, "idempotency_token", NpgsqlDbType.Char, idempotencyToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }


    public Task<EntitySyncPolicy?> GetAsync(
        string tenantId,
        Guid policyId,
        int version,
        CancellationToken cancellationToken) =>
        GetOneAsync(
            """
            SELECT tenant_id, policy_id, version, name, route_scope, definition::text,
                   definition_sha256, enabled, created_at, created_by
            FROM entitysync.sync_policies
            WHERE tenant_id = @tenant_id AND policy_id = @policy_id AND version = @version
            """,
            tenantId, policyId, version, cancellationToken);

    public Task<EntitySyncPolicy?> GetLatestAsync(
        string tenantId,
        Guid policyId,
        CancellationToken cancellationToken) =>
        GetOneAsync(
            """
            SELECT tenant_id, policy_id, version, name, route_scope, definition::text,
                   definition_sha256, enabled, created_at, created_by
            FROM entitysync.sync_policies
            WHERE tenant_id = @tenant_id AND policy_id = @policy_id
            ORDER BY version DESC
            LIMIT 1
            """,
            tenantId, policyId, null, cancellationToken);

    public async Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(
        string tenantId,
        string? routeScope,
        bool? enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT latest.tenant_id, latest.policy_id, latest.version, latest.name,
                   latest.route_scope, latest.definition::text,
                   latest.definition_sha256, latest.enabled, latest.created_at,
                   latest.created_by
            FROM (
                SELECT DISTINCT ON (policy_id)
                       tenant_id, policy_id, version, name, route_scope, definition,
                       definition_sha256, enabled, created_at, created_by
                FROM entitysync.sync_policies
                WHERE tenant_id = @tenant_id
                ORDER BY policy_id, version DESC
            ) latest
            WHERE latest.tenant_id = @tenant_id
              AND (@route_scope IS NULL OR latest.route_scope = @route_scope)
              AND (@enabled IS NULL OR latest.enabled = @enabled)
            ORDER BY latest.policy_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "route_scope", NpgsqlDbType.Text, routeScope);
        PostgresControlPersistence.Add(command, "enabled", NpgsqlDbType.Boolean, enabled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EntitySyncPolicy>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlyList<EntitySyncPolicy>> ListVersionsAsync(
        string tenantId,
        Guid policyId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumRows is <= 0 or > 101)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            SELECT tenant_id, policy_id, version, name, route_scope, definition::text,
                   definition_sha256, enabled, created_at, created_by
            FROM entitysync.sync_policies
            WHERE tenant_id = @tenant_id AND policy_id = @policy_id
            ORDER BY version DESC
            LIMIT @maximum_rows OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, policyId);
        PostgresControlPersistence.Add(
            command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        PostgresControlPersistence.Add(command, "offset", NpgsqlDbType.Integer, offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<EntitySyncPolicy>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    private async Task<EntitySyncPolicy?> GetOneAsync(
        string sql,
        string tenantId,
        Guid policyId,
        int? version,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, policyId);
        if (version is not null)
            PostgresControlPersistence.Add(command, "version", NpgsqlDbType.Integer, version.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static void AddPolicy(NpgsqlCommand command, EntitySyncPolicy policy)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, policy.TenantId);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, policy.PolicyId);
        PostgresControlPersistence.Add(command, "version", NpgsqlDbType.Integer, policy.Version);
        PostgresControlPersistence.Add(command, "name", NpgsqlDbType.Text, policy.Name);
        PostgresControlPersistence.Add(command, "route_scope", NpgsqlDbType.Text, policy.RouteScope);
        PostgresControlPersistence.Add(command, "definition", NpgsqlDbType.Jsonb, Serialize(policy.Definition));
        PostgresControlPersistence.Add(command, "definition_sha256", NpgsqlDbType.Char, policy.DefinitionSha256.Value);
        PostgresControlPersistence.Add(command, "enabled", NpgsqlDbType.Boolean, policy.Enabled);
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, policy.CreatedAt);
        PostgresControlPersistence.Add(command, "created_by", NpgsqlDbType.Text, policy.CreatedBy.ActorId);
    }

    private static async Task LockPolicyIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid policyId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(hashtextextended(@policy_identity, 1))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(
            command,
            "policy_identity",
            NpgsqlDbType.Text,
            $"{tenantId}:{policyId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EntitySyncPolicy Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), Deserialize(reader.GetString(5)),
            new EntitySyncSha256(reader.GetString(6)), reader.GetBoolean(7),
            reader.GetFieldValue<DateTimeOffset>(8), new EntitySyncActor(reader.GetString(9)));

    private static string Serialize(EntitySyncPolicyDefinition definition) => JsonSerializer.Serialize(
        new PolicyDefinitionStorage(
            definition.SourceVendor, definition.SourceConnectionId, definition.SourceEntityType,
            definition.TargetVendor, definition.TargetConnectionId, definition.TargetEntityType,
            definition.IncludeInactive, definition.CreateMissing, definition.AutoLinkScore,
            definition.ReviewScore, definition.SourceExternalIdName, definition.TargetCustomFieldName,
            definition.UpdatePolicy,
            definition.AllowedFields
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            definition.BlockedFields
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            definition.ScheduledApplySafeSubset));

    private static EntitySyncPolicyDefinition Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<PolicyDefinitionStorage>(json)
            ?? throw new InvalidOperationException("Stored policy definition is null.");
        return new EntitySyncPolicyDefinition(
            stored.SourceVendor, stored.SourceConnectionId, stored.SourceEntityType,
            stored.TargetVendor, stored.TargetConnectionId, stored.TargetEntityType,
            stored.IncludeInactive, stored.CreateMissing, stored.AutoLinkScore, stored.ReviewScore,
            stored.SourceExternalIdName, stored.TargetCustomFieldName, stored.UpdatePolicy,
            stored.AllowedFields, stored.BlockedFields, stored.ScheduledApplySafeSubset);
    }

    private sealed record PolicyDefinitionStorage(
        string SourceVendor,
        string SourceConnectionId,
        string SourceEntityType,
        string TargetVendor,
        string TargetConnectionId,
        string TargetEntityType,
        bool IncludeInactive,
        bool CreateMissing,
        int AutoLinkScore,
        int ReviewScore,
        string? SourceExternalIdName,
        string? TargetCustomFieldName,
        EntitySyncUpdatePolicy UpdatePolicy,
        string[] AllowedFields,
        string[] BlockedFields,
        bool ScheduledApplySafeSubset);
}
