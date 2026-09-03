using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresConnectionDefinitionRepository(NpgsqlDataSource dataSource)
    : IConnectionDefinitionRepository
{
    public async Task<EntitySyncConnectionDefinition> InsertAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(
            tenantId,
            definition.TenantId,
            nameof(definition));
        await using var lease = await PostgresControlTransaction
            .AcquireAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var transaction = lease.Transaction;
        const string existingSql = """
            SELECT 1
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            FOR UPDATE
            """;
        await using (var existing =
            new NpgsqlCommand(existingSql, connection, transaction))
        {
            AddKey(existing, tenantId, definition.ConnectionId);
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is not null)
                throw new InvalidOperationException(
                    $"Connection '{definition.ConnectionId}' already exists.");
        }
        const string generationSql = """
            INSERT INTO entitysync.connection_generation_counters (
                tenant_id, connection_id, last_generation)
            VALUES (@tenant_id, @connection_id, 1)
            ON CONFLICT (tenant_id, connection_id) DO UPDATE
            SET last_generation =
                entitysync.connection_generation_counters.last_generation + 1
            RETURNING last_generation
            """;
        long generation;
        await using (var generationCommand =
            new NpgsqlCommand(generationSql, connection, transaction))
        {
            AddKey(generationCommand, tenantId, definition.ConnectionId);
            generation = (long)(await generationCommand
                .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        var persisted = WithGeneration(definition, generation);
        const string insertSql = """
            INSERT INTO entitysync.connection_definitions (
                tenant_id, connection_id, vendor, display_name, generation, enabled,
                public_configuration, secret_ciphertext, created_at, created_by,
                updated_at, updated_by, platform_instance_id)
            VALUES (
                @tenant_id, @connection_id, @vendor, @display_name, @generation, @enabled,
                @public_configuration, @secret_ciphertext, @created_at, @created_by,
                @updated_at, @updated_by, @platform_instance_id)
            """;
        await using (var insert =
            new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddDefinition(insert, persisted);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    public async Task<EntitySyncConnectionDefinition?> GetAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, connection_id, vendor, display_name, generation, enabled,
                   public_configuration::text, secret_ciphertext, created_at, created_by,
                   updated_at, updated_by, platform_instance_id
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
                   updated_at, updated_by, platform_instance_id
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

    public async Task<EntitySyncConnectionDefinition?> TryReplaceAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncConnectionDefinition nextGeneration,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(
            tenantId,
            nextGeneration.TenantId,
            nameof(nextGeneration));
        if (!string.Equals(
                connectionId,
                nextGeneration.ConnectionId,
                StringComparison.Ordinal))
            throw new ArgumentException(
                "The replacement connection ID must match.",
                nameof(nextGeneration));
        await using var lease = await PostgresControlTransaction
            .AcquireAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var transaction = lease.Transaction;
        const string lockSql = """
            SELECT generation
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            FOR UPDATE
            """;
        await using (var lockCommand = new NpgsqlCommand(lockSql, connection, transaction))
        {
            AddKey(lockCommand, tenantId, connectionId);
            var current = await lockCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null || (long)current != expectedGeneration)
            {
                await lease.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        const string generationSql = """
            UPDATE entitysync.connection_generation_counters
            SET last_generation = last_generation + 1
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            RETURNING last_generation
            """;
        long generation;
        await using (var generationCommand =
            new NpgsqlCommand(generationSql, connection, transaction))
        {
            AddKey(generationCommand, tenantId, connectionId);
            generation = (long)(await generationCommand
                .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The connection generation counter is missing."));
        }
        var persisted = WithGeneration(nextGeneration, generation);
        const string updateSql = """
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
                updated_by = @updated_by,
                platform_instance_id = @platform_instance_id
            WHERE tenant_id = @tenant_id
              AND connection_id = @connection_id
              AND generation = @expected_generation
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddDefinition(update, persisted);
            PostgresControlPersistence.Add(
                update,
                "expected_generation",
                NpgsqlDbType.Bigint,
                expectedGeneration);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException(
                    "The locked connection generation changed unexpectedly.");
        }
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    public async Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await using var lease = await PostgresControlTransaction
            .AcquireAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var transaction = lease.Transaction;
        const string lockSql = """
            SELECT generation
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            FOR UPDATE
            """;
        await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
        AddKey(lockCommand, tenantId, connectionId);
        var generationValue = await lockCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (generationValue is null)
        {
            await lease.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ConnectionDefinitionDeleteResult.NotFound;
        }
        if ((long)generationValue != expectedGeneration)
        {
            await lease.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ConnectionDefinitionDeleteResult.GenerationMismatch;
        }

        const string referenceSql = """
            SELECT EXISTS (
                SELECT 1
                FROM entitysync.sync_policies
                WHERE tenant_id = @tenant_id
                  AND (
                    definition ->> 'SourceConnectionId' = @connection_id
                    OR definition ->> 'TargetConnectionId' = @connection_id
                  )
                UNION ALL
                SELECT 1
                FROM entitysync.sync_plans
                WHERE tenant_id = @tenant_id
                  AND (
                    source_connection_id = @connection_id
                    OR target_connection_id = @connection_id
                  )
            )
            """;
        await using var referenceCommand = new NpgsqlCommand(
            referenceSql,
            connection,
            transaction);
        AddKey(referenceCommand, tenantId, connectionId);
        if ((bool)(await referenceCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!)
        {
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ConnectionDefinitionDeleteResult.Referenced;
        }

        const string deleteSql = """
            DELETE FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id
              AND connection_id = @connection_id
              AND generation = @expected_generation
            """;
        await using var deleteCommand = new NpgsqlCommand(
            deleteSql,
            connection,
            transaction);
        AddKey(deleteCommand, tenantId, connectionId);
        PostgresControlPersistence.Add(
            deleteCommand,
            "expected_generation",
            NpgsqlDbType.Bigint,
            expectedGeneration);
        var deleted = await deleteCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 1
            ? ConnectionDefinitionDeleteResult.Deleted
            : ConnectionDefinitionDeleteResult.GenerationMismatch;
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
        PostgresControlPersistence.Add(
            command,
            "platform_instance_id",
            NpgsqlDbType.Uuid,
            definition.PlatformInstanceId);
    }

    private static EntitySyncConnectionDefinition WithGeneration(
        EntitySyncConnectionDefinition definition,
        long generation) =>
        new(
            definition.TenantId,
            definition.ConnectionId,
            definition.Vendor,
            definition.DisplayName,
            generation,
            definition.Enabled,
            definition.PublicConfiguration,
            definition.SecretCiphertext,
            definition.CreatedAt,
            definition.CreatedBy,
            definition.UpdatedAt,
            definition.UpdatedBy,
            definition.PlatformInstanceId);

    private static EntitySyncConnectionDefinition Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetBoolean(5), new EntitySyncJsonValue(reader.GetString(6)),
            reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8),
            new EntitySyncActor(reader.GetString(9)), reader.GetFieldValue<DateTimeOffset>(10),
            new EntitySyncActor(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetGuid(12));
}
