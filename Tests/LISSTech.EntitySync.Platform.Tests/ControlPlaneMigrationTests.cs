using LISSTech.EntitySync.Runtime;
using Npgsql;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlPlaneMigrationTests : IAsyncLifetime
{
    private readonly string _databaseName = $"entitysync_migration_{Guid.NewGuid():N}";
    private NpgsqlDataSource? _adminDataSource;
    private NpgsqlDataSource? _database;

    [Fact]
    public async Task Migrations_create_control_plane_and_are_idempotent()
    {
        await MigrateAsync();
        await MigrateAsync();

        var tables = await ListTablesAsync("entitysync");
        AssertSuperset(tables,
        [
            "entity_exclusions", "entity_change_state", "connection_definitions",
            "sync_policies", "sync_plans", "sync_plan_items", "sync_plan_inspections",
            "sync_approvals", "sync_operations", "sync_operation_items", "sync_schedules",
            "canonical_change_events", "api_idempotency_records", "audit_events"
        ]);
        Assert.Equal(
            ["001_entity_exclusions", "002_entity_change_state", "003_harden_entity_change_state_key", "004_control_plane", "005_control_operations", "006_control_audit_scheduler"],
            await ListAppliedMigrationsAsync());
        Assert.Equal(1, await CountAsync("entitysync.entity_exclusions"));
        Assert.Equal(1, await CountAsync("entitysync.entity_change_state"));

        Assert.True(await HasPrimaryKeyAsync("entitysync", "sync_operations", "sync_operation_pkey"));
        Assert.True(await HasConstraintOrTriggerAsync("entitysync", "audit_events", "audit_events_immutable"));

        foreach (var table in new[]
                 {
                     "connection_definitions", "sync_policies", "sync_plans", "sync_plan_items",
                     "sync_plan_inspections", "sync_approvals", "sync_operations",
                     "sync_operation_items", "sync_schedules", "canonical_change_events",
                     "api_idempotency_records", "audit_events"
                 })
        {
            Assert.True(await PrimaryKeyContainsColumnAsync("entitysync", table, "tenant_id"),
                $"{table} must include tenant_id in its primary key.");
        }

        Assert.True(await HasCheckAsync("entitysync", "sync_plans", "sync_plans_status_check",
            "Draft", "Approved", "Consumed", "Expired"));
        Assert.True(await HasCheckAsync("entitysync", "sync_operations", "sync_operations_mode_check",
            "DryRun", "Apply"));
        Assert.True(await HasCheckAsync("entitysync", "sync_operations", "sync_operations_status_check",
            "Queued", "Leased", "Running", "Succeeded", "Partial", "Failed", "Cancelled"));
        Assert.True(await HasCheckAsync("entitysync", "sync_operation_items", "sync_operation_items_outcome_check",
            "Pending", "Succeeded", "Failed", "Skipped", "Unknown"));
        Assert.True(await HasUniqueIndexAsync("entitysync", "sync_operations", "sync_operations_tenant_id_idempotency_key_key"));
        Assert.True(await HasUniqueIndexAsync("entitysync", "sync_approvals", "sync_approvals_tenant_id_plan_digest_sha256_key"));

        await AssertAuditMutationRejectedAsync();
    }

    [Fact]
    public async Task Approval_digest_must_match_its_plan()
    {
        await MigrateAsync();
        await SeedPlansAsync();

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, plan_id, plan_digest_sha256, approved_at, approved_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000102',
                '00000000-0000-0000-0000-000000000101', repeat('2', 64), now(), 'tester');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task Inspection_digest_must_match_its_plan()
    {
        await MigrateAsync();
        await SeedPlansAsync();

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                range_start, range_end, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000201',
                '00000000-0000-0000-0000-000000000101', repeat('2', 64),
                0, 10, now(), 'tester');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task Operation_approval_must_authorize_the_same_plan()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000112',
                'route-a', 'Apply', 'Queued', 'operation-mismatch', 0, now(), now());
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task Operation_item_identity_is_immutable_while_results_can_advance()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();
        await using (var seed = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000111',
                'route-a', 'Apply', 'Queued', 'operation-valid', 0, now(), now());

            INSERT INTO entitysync.sync_operation_items (
                tenant_id, operation_id, item_id, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                redacted_before, redacted_desired, desired_payload_sha256, outcome)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                'company', 'entity-1', 'ENTITY-1', 'target', 'target-1', 'account',
                'TARGET-1', 'Update', '{}', '{}', repeat('3', 64), 'Pending');
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var mutateIdentity = Database.CreateCommand("""
            UPDATE entitysync.sync_operation_items
            SET source_entity_id = 'MUTATED', outcome = 'Succeeded'
            WHERE tenant_id = 'tenant-a'
              AND operation_id = '00000000-0000-0000-0000-000000000301'
              AND item_id = '00000000-0000-0000-0000-000000000302';
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => mutateIdentity.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);

        await using var advanceResult = Database.CreateCommand("""
            UPDATE entitysync.sync_operation_items
            SET outcome = 'Succeeded', completed_at = now()
            WHERE tenant_id = 'tenant-a'
              AND operation_id = '00000000-0000-0000-0000-000000000301'
              AND item_id = '00000000-0000-0000-0000-000000000302';
            """);
        Assert.Equal(1, await advanceResult.ExecuteNonQueryAsync());
    }

    public async Task InitializeAsync()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            var user = Environment.UserName;
            adminConnectionString = $"Host=127.0.0.1;Database=postgres;Username={user};Pooling=false";
        }

        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        _adminDataSource = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        await ExecuteAdminAsync($"CREATE DATABASE \"{_databaseName}\"");

        var databaseBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = _databaseName,
            Pooling = false
        };
        _database = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
        await ApplyLegacyMigrationsAsync();
        await SeedExistingMigrationDataAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
        if (_adminDataSource is not null)
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
            await _adminDataSource.DisposeAsync();
        }
    }

    private Task MigrateAsync() => EntitySyncDatabaseMigrator.ApplyAsync(Database);
    private async Task ApplyLegacyMigrationsAsync()
    {
        var assembly = typeof(EntitySyncDatabaseMigrator).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Migrations.00", StringComparison.Ordinal)
                                    && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.Ordinal)
                     .Take(3))
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            await using var migration = Database.CreateCommand(sql);
            await migration.ExecuteNonQueryAsync();

            var version = Path.GetFileNameWithoutExtension(
                resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + 12)..]);
            await using var record = Database.CreateCommand(
                "INSERT INTO entitysync.schema_migrations (version) VALUES (@version)");
            record.Parameters.AddWithValue("version", version);
            await record.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedPlansAsync()
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_policies (
                tenant_id, policy_id, version, name, route_scope, definition,
                definition_sha256, enabled, created_at, created_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000100', 1, 'policy-a',
                'route-a', '{}', repeat('0', 64), true, now(), 'tester');

            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000101',
                 '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                 repeat('1', 64), 'Draft', now(), 'tester', now() + interval '1 day'),
                ('tenant-a', '00000000-0000-0000-0000-000000000102',
                 '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                 repeat('2', 64), 'Draft', now(), 'tester', now() + interval '1 day');
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedPlansAndApprovalsAsync()
    {
        await SeedPlansAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, plan_id, plan_digest_sha256, approved_at, approved_by)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000111',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64), now(), 'tester'),
                ('tenant-a', '00000000-0000-0000-0000-000000000112',
                 '00000000-0000-0000-0000-000000000102', repeat('2', 64), now(), 'tester');
            """);
        await command.ExecuteNonQueryAsync();
    }


    private async Task SeedExistingMigrationDataAsync()
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.entity_exclusions (
                id, tenant_id, source_vendor, source_connection_id, source_entity_type,
                target_vendor, target_connection_id, target_entity_type, source_entity_key,
                source_entity_id, source_name, reason, created_by)
            VALUES (
                '00000000-0000-0000-0000-000000000001', 'tenant-a', 'source', 'source-1', 'company',
                'target', 'target-1', 'account', 'entity-1', 'ENTITY-1', 'Existing entity',
                'migration preservation', 'test');

            INSERT INTO entitysync.entity_change_state (
                tenant_id, route_scope, source_vendor, source_connection_id, source_entity_type,
                target_vendor, target_connection_id, target_entity_type, source_entity_key,
                source_entity_id, source_name, target_entity_id, hash_version, payload_hash, applied_at)
            VALUES (
                'tenant-a', repeat('a', 64), 'source', 'source-1', 'company',
                'target', 'target-1', 'account', 'entity-1', 'ENTITY-1', 'Existing entity',
                'TARGET-1', 1, repeat('b', 64), now());
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertAuditMutationRejectedAsync()
    {
        await using var insert = Database.CreateCommand("""
            INSERT INTO entitysync.audit_events (
                tenant_id, audit_event_id, occurred_at, event_type, actor_id, operation_id,
                run_id, plan_id, item_id, correlation_id, redacted_values,
                redacted_values_sha256, full_values_ciphertext, full_values_sha256,
                full_values_expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000010', now(), 'OperationQueued',
                'tester', '00000000-0000-0000-0000-000000000020',
                '00000000-0000-0000-0000-000000000030',
                '00000000-0000-0000-0000-000000000040',
                '00000000-0000-0000-0000-000000000050', 'correlation-1', '{"secret":"[redacted]"}',
                repeat('c', 64), 'ciphertext', repeat('d', 64), now() + interval '365 days');
            """);
        await insert.ExecuteNonQueryAsync();

        await using var update = Database.CreateCommand("""
            UPDATE entitysync.audit_events SET actor_id = 'mutated'
            WHERE tenant_id = 'tenant-a'
              AND audit_event_id = '00000000-0000-0000-0000-000000000010';
            """);
        var updateError = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("55000", updateError.SqlState);

        await using var delete = Database.CreateCommand("""
            DELETE FROM entitysync.audit_events
            WHERE tenant_id = 'tenant-a'
              AND audit_event_id = '00000000-0000-0000-0000-000000000010';
            """);
        var deleteError = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("55000", deleteError.SqlState);
    }

    private async Task<string[]> ListTablesAsync(string schema)
    {
        await using var command = Database.CreateCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_type = 'BASE TABLE'
            ORDER BY table_name;
            """);
        command.Parameters.AddWithValue("schema", schema);
        return await ReadStringsAsync(command);
    }

    private async Task<string[]> ListAppliedMigrationsAsync()
    {
        await using var command = Database.CreateCommand(
            "SELECT version FROM entitysync.schema_migrations ORDER BY version");
        return await ReadStringsAsync(command);
    }

    private async Task<int> CountAsync(string qualifiedTable)
    {
        await using var command = Database.CreateCommand($"SELECT count(*)::integer FROM {qualifiedTable}");
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private async Task<bool> HasPrimaryKeyAsync(string schema, string table, string constraint)
    {
        await using var command = Database.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.table_constraints
                WHERE table_schema = @schema AND table_name = @table
                  AND constraint_name = @constraint AND constraint_type = 'PRIMARY KEY');
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("constraint", constraint);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private async Task<bool> PrimaryKeyContainsColumnAsync(string schema, string table, string column)
    {
        await using var command = Database.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_schema = tc.constraint_schema
                 AND kcu.constraint_name = tc.constraint_name
                WHERE tc.table_schema = @schema AND tc.table_name = @table
                  AND tc.constraint_type = 'PRIMARY KEY' AND kcu.column_name = @column);
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private async Task<bool> HasConstraintOrTriggerAsync(string schema, string table, string name)
    {
        await using var command = Database.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_trigger trigger
                JOIN pg_class relation ON relation.oid = trigger.tgrelid
                JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema AND relation.relname = @table
                  AND trigger.tgname = @name AND NOT trigger.tgisinternal);
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private async Task<bool> HasCheckAsync(string schema, string table, string name, params string[] values)
    {
        await using var command = Database.CreateCommand("""
            SELECT pg_get_constraintdef(check_constraint.oid)
            FROM pg_constraint check_constraint
            JOIN pg_class relation ON relation.oid = check_constraint.conrelid
            JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema AND relation.relname = @table
              AND check_constraint.conname = @name AND check_constraint.contype = 'c';
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("name", name);
        var definition = (string?)await command.ExecuteScalarAsync();
        return definition is not null && values.All(value => definition.Contains(value, StringComparison.Ordinal));
    }

    private async Task<bool> HasUniqueIndexAsync(string schema, string table, string index)
    {
        await using var command = Database.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = @schema AND tablename = @table
                  AND indexname = @index AND indexdef LIKE 'CREATE UNIQUE INDEX%');
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("index", index);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var command = AdminDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadStringsAsync(NpgsqlCommand command)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void AssertSuperset(IEnumerable<string> actual, IEnumerable<string> expected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        foreach (var item in expected) Assert.Contains(item, actualSet);
    }

    private NpgsqlDataSource AdminDataSource =>
        _adminDataSource ?? throw new InvalidOperationException("The admin data source is not initialized.");

    private NpgsqlDataSource Database =>
        _database ?? throw new InvalidOperationException("The test database is not initialized.");
}
