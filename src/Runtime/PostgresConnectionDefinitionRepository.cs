using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresConnectionDefinitionRepository(NpgsqlDataSource dataSource)
    : IConnectionDefinitionRepository
{
    public async Task InsertAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, definition.TenantId, nameof(definition));
        const string sql = """
            INSERT INTO entitysync.connection_definitions (
                tenant_id, connection_id, vendor, display_name, generation, enabled,
                public_configuration, secret_ciphertext, created_at, created_by,
                updated_at, updated_by)
            VALUES (
                @tenant_id, @connection_id, @vendor, @display_name, @generation, @enabled,
                @public_configuration, @secret_ciphertext, @created_at, @created_by,
                @updated_at, @updated_by)
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddDefinition(command, definition);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntitySyncConnectionDefinition?> GetAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, connection_id, vendor, display_name, generation, enabled,
                   public_configuration::text, secret_ciphertext, created_at, created_by,
                   updated_at, updated_by
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddKey(command, tenantId, connectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
        string tenantId,
        string? vendor,
        bool? enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, connection_id, vendor, display_name, generation, enabled,
                   public_configuration::text, secret_ciphertext, created_at, created_by,
                   updated_at, updated_by
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id
              AND (@vendor IS NULL OR vendor = @vendor)
              AND (@enabled IS NULL OR enabled = @enabled)
            ORDER BY vendor, connection_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "vendor", NpgsqlDbType.Text,
            vendor is null ? null : EntitySyncVendors.Normalize(vendor));
        PostgresControlPersistence.Add(command, "enabled", NpgsqlDbType.Boolean, enabled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EntitySyncConnectionDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader));
        return result;
    }

    public async Task<bool> TryReplaceAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncConnectionDefinition nextGeneration,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, nextGeneration.TenantId, nameof(nextGeneration));
        if (!string.Equals(connectionId, nextGeneration.ConnectionId, StringComparison.Ordinal))
            throw new ArgumentException("The replacement connection ID must match.", nameof(nextGeneration));
        if (nextGeneration.Generation != checked(expectedGeneration + 1))
            throw new ArgumentException("The replacement must advance the expected generation exactly once.", nameof(nextGeneration));
        const string sql = """
            UPDATE entitysync.connection_definitions
            SET vendor = @vendor,
                display_name = @display_name,
                generation = @generation,
                enabled = @enabled,
                public_configuration = @public_configuration,
                secret_ciphertext = @secret_ciphertext,
                created_at = @created_at,
                created_by = @created_by,
                updated_at = @updated_at,
                updated_by = @updated_by
            WHERE tenant_id = @tenant_id
              AND connection_id = @connection_id
              AND generation = @expected_generation
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddDefinition(command, nextGeneration);
        PostgresControlPersistence.Add(command, "expected_generation", NpgsqlDbType.Bigint, expectedGeneration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddKey(NpgsqlCommand command, string tenantId, string connectionId)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "connection_id", NpgsqlDbType.Text, connectionId);
    }

    private static void AddDefinition(NpgsqlCommand command, EntitySyncConnectionDefinition definition)
    {
        AddKey(command, definition.TenantId, definition.ConnectionId);
        PostgresControlPersistence.Add(command, "vendor", NpgsqlDbType.Text, definition.Vendor);
        PostgresControlPersistence.Add(command, "display_name", NpgsqlDbType.Text, definition.DisplayName);
        PostgresControlPersistence.Add(command, "generation", NpgsqlDbType.Bigint, definition.Generation);
        PostgresControlPersistence.Add(command, "enabled", NpgsqlDbType.Boolean, definition.Enabled);
        PostgresControlPersistence.Add(command, "public_configuration", NpgsqlDbType.Jsonb, definition.PublicConfiguration.Json);
        PostgresControlPersistence.Add(command, "secret_ciphertext", NpgsqlDbType.Text, definition.SecretCiphertext);
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, definition.CreatedAt);
        PostgresControlPersistence.Add(command, "created_by", NpgsqlDbType.Text, definition.CreatedBy.ActorId);
        PostgresControlPersistence.Add(command, "updated_at", NpgsqlDbType.TimestampTz, definition.UpdatedAt);
        PostgresControlPersistence.Add(command, "updated_by", NpgsqlDbType.Text, definition.UpdatedBy.ActorId);
    }

    private static EntitySyncConnectionDefinition Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetBoolean(5), new EntitySyncJsonValue(reader.GetString(6)),
            reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8),
            new EntitySyncActor(reader.GetString(9)), reader.GetFieldValue<DateTimeOffset>(10),
            new EntitySyncActor(reader.GetString(11)));
}
