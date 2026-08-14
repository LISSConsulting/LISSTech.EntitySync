using System.Reflection;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresEntityExclusionRepository(NpgsqlDataSource dataSource) : IEntityExclusionRepository
{
    private const string RoutePredicate = """
        tenant_id = @tenant_id
        AND source_vendor = @source_vendor
        AND source_connection_id = @source_connection_id
        AND source_entity_type = @source_entity_type
        AND target_vendor = @target_vendor
        AND target_connection_id = @target_connection_id
        AND target_entity_type = @target_entity_type
        """;

    public async Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(EntityExclusionRoute route, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source_entity_id, source_name, reason, created_by, created_at
            FROM entitysync.entity_exclusions
            WHERE revoked_at IS NULL AND
            """ + "\n" + RoutePredicate + " ORDER BY source_entity_id";
        await using var command = dataSource.CreateCommand(sql);
        AddRoute(command, route);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var exclusions = new List<EntityExclusion>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            exclusions.Add(new EntityExclusion(
                reader.GetGuid(0),
                route,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return exclusions;
    }

    public async Task<EntityExclusion> AddAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string sourceName,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        sourceEntityId = Require(sourceEntityId, nameof(sourceEntityId), 512);
        sourceName = Require(sourceName, nameof(sourceName), 512);
        reason = Require(reason, nameof(reason), 2000);
        actor = Require(actor, nameof(actor), 256);
        var id = Guid.NewGuid();
        const string sql = """
            INSERT INTO entitysync.entity_exclusions (
                id, tenant_id, source_vendor, source_connection_id, source_entity_type,
                target_vendor, target_connection_id, target_entity_type, source_entity_key,
                source_entity_id, source_name, reason, created_by)
            VALUES (
                @id, @tenant_id, @source_vendor, @source_connection_id, @source_entity_type,
                @target_vendor, @target_connection_id, @target_entity_type, lower(@source_entity_id),
                @source_entity_id, @source_name, @reason, @actor)
            ON CONFLICT (
                tenant_id, source_vendor, source_connection_id, source_entity_type,
                target_vendor, target_connection_id, target_entity_type, source_entity_key)
            WHERE revoked_at IS NULL
            DO UPDATE SET source_name = EXCLUDED.source_name, reason = EXCLUDED.reason
            RETURNING id, created_by, created_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddRoute(command, route);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("source_entity_id", sourceEntityId);
        command.Parameters.AddWithValue("source_name", sourceName);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("actor", actor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("The exclusion was not stored.");
        return new EntityExclusion(reader.GetGuid(0), route, sourceEntityId, sourceName, reason, reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<bool> RevokeAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string actor,
        CancellationToken cancellationToken)
    {
        sourceEntityId = Require(sourceEntityId, nameof(sourceEntityId), 512);
        actor = Require(actor, nameof(actor), 256);
        const string sql = """
            UPDATE entitysync.entity_exclusions
            SET revoked_by = @actor, revoked_at = now()
            WHERE revoked_at IS NULL AND source_entity_key = lower(@source_entity_id) AND
            """ + "\n" + RoutePredicate;
        await using var command = dataSource.CreateCommand(sql);
        AddRoute(command, route);
        command.Parameters.AddWithValue("source_entity_id", sourceEntityId);
        command.Parameters.AddWithValue("actor", actor);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddRoute(NpgsqlCommand command, EntityExclusionRoute route)
    {
        command.Parameters.AddWithValue("tenant_id", route.TenantId);
        command.Parameters.AddWithValue("source_vendor", route.SourceVendor);
        command.Parameters.AddWithValue("source_connection_id", route.SourceConnectionId);
        command.Parameters.AddWithValue("source_entity_type", route.SourceEntityType);
        command.Parameters.AddWithValue("target_vendor", route.TargetVendor);
        command.Parameters.AddWithValue("target_connection_id", route.TargetConnectionId);
        command.Parameters.AddWithValue("target_entity_type", route.TargetEntityType);
    }

    private static string Require(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength) throw new ArgumentException($"{name} cannot exceed {maximumLength} characters.", name);
        return trimmed;
    }
}

public static class EntitySyncDatabaseMigrator
{
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var advisoryConnection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var advisoryLock = advisoryConnection.CreateCommand();
        advisoryLock.CommandText = "SELECT pg_advisory_lock(hashtextextended('lisstech.entitysync.migrations', 0))";
        await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var bootstrap = advisoryConnection.CreateCommand();
            bootstrap.CommandText = "CREATE SCHEMA IF NOT EXISTS entitysync; CREATE TABLE IF NOT EXISTS entitysync.schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())";
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var resourceName in typeof(EntitySyncDatabaseMigrator).Assembly.GetManifestResourceNames()
                         .Where(name => name.Contains(".Database.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + 12)..]);
                await using var exists = advisoryConnection.CreateCommand();
                exists.CommandText = "SELECT EXISTS (SELECT 1 FROM entitysync.schema_migrations WHERE version = @version)";
                exists.Parameters.AddWithValue("version", version);
                if ((bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false)) continue;

                await using var transaction = await advisoryConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = typeof(EntitySyncDatabaseMigrator).Assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await using var migration = new NpgsqlCommand(sql, advisoryConnection, transaction);
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await using var record = new NpgsqlCommand("INSERT INTO entitysync.schema_migrations (version) VALUES (@version)", advisoryConnection, transaction);
                record.Parameters.AddWithValue("version", version);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await using var unlock = advisoryConnection.CreateCommand();
            unlock.CommandText = "SELECT pg_advisory_unlock(hashtextextended('lisstech.entitysync.migrations', 0))";
            await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
