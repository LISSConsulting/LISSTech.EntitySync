using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mcp.ControlApi;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
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
            "connection_generation_counters", "sync_policies", "sync_plans",
            "sync_plan_creation_claims", "sync_plan_items", "sync_plan_inspections",
            "sync_plan_inspection_ranges", "sync_approvals", "sync_operations",
            "sync_operation_items", "sync_operation_item_snapshots", "sync_schedules",
            "canonical_change_events", "api_idempotency_records",
            "plan_import_receipts", "audit_events", "audit_event_full_values"
        ]);
        Assert.Equal(
            EntitySyncDatabaseMigrator.ExpectedVersions,
            await ListAppliedMigrationsAsync());
        Assert.Equal(1, await CountAsync("entitysync.entity_exclusions"));
        Assert.Equal(1, await CountAsync("entitysync.entity_change_state"));

        Assert.True(await HasPrimaryKeyAsync("entitysync", "sync_operations", "sync_operation_pkey"));

        Assert.True(await HasConstraintOrTriggerAsync("entitysync", "audit_events", "audit_events_immutable"));

        foreach (var table in new[]
                 {
                     "connection_definitions", "sync_policies", "sync_plans",
                     "sync_plan_creation_claims", "sync_plan_items", "sync_plan_inspections",
                     "sync_plan_inspection_ranges", "sync_approvals", "sync_operations",
                     "sync_operation_items", "sync_operation_item_snapshots", "sync_schedules",
                     "canonical_change_events", "api_idempotency_records",
                     "plan_import_receipts", "audit_events", "audit_event_full_values"
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
        Assert.True(await HasCheckAsync(

            "entitysync",
            "sync_operation_items",
            "sync_operation_items_outcome_check",
            "Pending",
            "Succeeded",
            "Failed",
            "Skipped",
            "Unknown"));
        Assert.True(await HasCheckAsync(
            "entitysync",
            "sync_plan_creation_claims",
            "sync_plan_creation_claims_state_check",
            "InProgress",
            "Completed"));
        Assert.True(await HasUniqueIndexAsync("entitysync", "sync_operations", "sync_operations_tenant_id_idempotency_key_key"));
        Assert.True(await HasUniqueIndexAsync("entitysync", "sync_approvals", "sync_approvals_tenant_id_plan_digest_sha256_key"));

        await AssertAuditMutationRejectedAsync();
    }
    [Fact]
    public async Task Readiness_requires_the_exact_embedded_migration_version_set()
    {
        await MigrateAsync();
        await using (var heartbeat = Database.CreateCommand("""
                         INSERT INTO entitysync.control_worker_heartbeats (worker_id, observed_at)
                         VALUES ('readiness-test', clock_timestamp())
                         """))
            await heartbeat.ExecuteNonQueryAsync();

        var probe = new ControlReadinessProbe(
            Database,
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            new EntitySyncWorkerSettings(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5)));
        Assert.True((await probe.CheckAsync(default)).DatabaseMigrations);

        await SetMigrationVersionAsync("001_entity_exclusions", present: false);
        Assert.False((await probe.CheckAsync(default)).DatabaseMigrations);
        await SetMigrationVersionAsync("001_entity_exclusions", present: true);

        await SetMigrationVersionAsync("018_snapshot_evidence_enrichment", present: false);
        Assert.False((await probe.CheckAsync(default)).DatabaseMigrations);
        await SetMigrationVersionAsync("018_snapshot_evidence_enrichment", present: true);

        await SetMigrationVersionAsync("999_unknown_rollback_drift", present: true);
        Assert.False((await probe.CheckAsync(default)).DatabaseMigrations);
        await SetMigrationVersionAsync("999_unknown_rollback_drift", present: false);
        Assert.True((await probe.CheckAsync(default)).DatabaseMigrations);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            SetMigrationVersionAsync("018_snapshot_evidence_enrichment", present: true));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Migration_008_reapplies_overlap_aware_inspection_completion_on_upgrade()
    {
        await MigrateAsync();
        await using (var replace = Database.CreateCommand(
                         """
                         CREATE OR REPLACE FUNCTION entitysync.enforce_inspection_completion()
                         RETURNS trigger
                         LANGUAGE plpgsql
                         AS $$
                         BEGIN
                             RAISE EXCEPTION 'legacy sentinel';
                         END;
                         $$;
                         DELETE FROM entitysync.schema_migrations
                         WHERE version = '008_plan_exclusion_serialization';
                         """))
            await replace.ExecuteNonQueryAsync();

        await MigrateAsync();

        await using var definition = Database.CreateCommand(
            """
            SELECT pg_get_functiondef(
                'entitysync.enforce_inspection_completion()'::regprocedure)
            """);
        var sql = Assert.IsType<string>(await definition.ExecuteScalarAsync());
        Assert.Contains("previous_max_end", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy sentinel", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_digest_must_match_its_plan()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await using (var inspection = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000201',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000201',
                '00000000-0000-0000-0000-000000000211', 0, 0, now());

            UPDATE entitysync.sync_plan_inspections
            SET status = 'Completed', completed_at = now()
            WHERE tenant_id = 'tenant-a'
              AND inspection_id = '00000000-0000-0000-0000-000000000201';
            """))
        {
            await inspection.ExecuteNonQueryAsync();
        }

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000111',
                '00000000-0000-0000-0000-000000000201',
                '00000000-0000-0000-0000-000000000101', repeat('2', 64),
                7, 11, now(), 'tester');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Inspection_digest_must_match_its_plan()
    {
        await MigrateAsync();
        await SeedPlansAsync();

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000201',
                '00000000-0000-0000-0000-000000000101', repeat('2', 64),
                7, 11, 'Open', now(), 'tester');
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
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000112',
                'route-a', 'Apply', 'Queued', 'operation-mismatch', 7, 11, 0, now(), now());
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
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000111',
                'route-a', 'Apply', 'Queued', 'operation-valid', 7, 11, 0, now(), now());

            INSERT INTO entitysync.sync_operation_items (
                tenant_id, operation_id, plan_id, item_id, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                redacted_before, redacted_desired, desired_payload_sha256,
                snapshots_expires_at, outcome)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                'company', 'entity-1', 'ENTITY-1', 'target', 'target-1', 'account',
                'TARGET-1', 'Update', '{}', '{}', repeat('3', 64),
                now() + interval '365 days', 'Pending');
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

    [Fact]
    public async Task Operation_items_must_match_their_approved_plan_item()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();

        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000111', 'route-a', 'Apply', 'Queued',
                'operation-item-mismatch', 7, 11, 0, now(), now());

            INSERT INTO entitysync.sync_operation_items (
                tenant_id, operation_id, plan_id, item_id, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                redacted_before, redacted_desired, desired_payload_sha256,
                snapshots_expires_at, outcome)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                'company', 'entity-1', 'DIFFERENT-ENTITY', 'target', 'target-1', 'account',
                'TARGET-1', 'Update', '{}', '{}', repeat('3', 64),
                now() + interval '365 days', 'Pending');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Apply_operations_require_and_consume_one_approval()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();

        await using var missingApproval = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101', NULL, 'route-a', 'Apply', 'Queued',
                'missing-approval', 7, 11, 0, now(), now());
            """);
        var missingError = await Assert.ThrowsAsync<PostgresException>(() => missingApproval.ExecuteNonQueryAsync());
        Assert.Equal("23514", missingError.SqlState);

        await using var consume = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000302',
                 '00000000-0000-0000-0000-000000000101',
                 '00000000-0000-0000-0000-000000000111', 'route-a', 'Apply', 'Queued',
                 'consume-approval-1', 7, 11, 0, now(), now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000303',
                 '00000000-0000-0000-0000-000000000101',
                 '00000000-0000-0000-0000-000000000111', 'route-a', 'Apply', 'Queued',
                 'consume-approval-2', 7, 11, 0, now(), now());
            """);
        var consumedError = await Assert.ThrowsAsync<PostgresException>(() => consume.ExecuteNonQueryAsync());
        Assert.Equal("23505", consumedError.SqlState);
    }

    [Fact]
    public async Task Approvals_reject_partial_inspection_coverage()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                match_score, match_type, match_reasons, field_diffs,
                redacted_before, redacted_desired, desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000303', 1, 'source', 'source-1',
                'company', 'entity-2', 'ENTITY-2', 'target', 'target-1', 'account',
                'TARGET-2', 'Update', 80, 'Fuzzy', '[]', '[]',
                '{}', '{}', repeat('4', 64));

            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000203',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000203',
                '00000000-0000-0000-0000-000000000213', 0, 0, now());

            UPDATE entitysync.sync_plan_inspections
            SET status = 'Completed', completed_at = now()
            WHERE tenant_id = 'tenant-a'
              AND inspection_id = '00000000-0000-0000-0000-000000000203';
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Connection_generations_are_positive_and_bound_to_operations()
    {
        await MigrateAsync();
        await SeedConnectionPolicyAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                'source-1', 'target-1', 0, 11, repeat('1', 64),
                'Draft', now(), 'tester', now() + interval '1 day');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Approval_requires_a_completed_inspection()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000111',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, now(), 'tester');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Operation_generations_must_match_the_approved_plan()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000111',
                'route-a', 'Apply', 'Queued', 'generation-mismatch', 13, 17, 0, now(), now());
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task Inspected_plan_items_cannot_be_extended_after_approval()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                match_score, match_type, match_reasons, field_diffs,
                redacted_before, redacted_desired, desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000304', 1, 'source', 'source-1',
                'company', 'entity-2', 'ENTITY-2', 'target', 'target-1', 'account',
                'TARGET-2', 'Update', 80, 'Fuzzy', '[]', '[]',
                '{}', '{}', repeat('4', 64));
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Expired_ciphertext_is_removable_without_deleting_metadata()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope, mode, status,
                idempotency_key, source_connection_generation, target_connection_generation,
                attempt, created_at, queued_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000111',
                'route-a', 'Apply', 'Succeeded', 'retention-operation', 7, 11,
                1, now() - interval '2 days', now() - interval '2 days');

            INSERT INTO entitysync.sync_operation_items (
                tenant_id, operation_id, plan_id, item_id, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                redacted_before, redacted_desired, desired_payload_sha256,
                after_payload_sha256, snapshots_expires_at, outcome, completed_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                'company', 'entity-1', 'ENTITY-1', 'target', 'target-1', 'account',
                'TARGET-1', 'Update', '{}', '{}', repeat('3', 64), repeat('5', 64),
                now() - interval '1 day', 'Succeeded', now() - interval '2 days');

            INSERT INTO entitysync.sync_operation_item_snapshots (
                tenant_id, operation_id, item_id,
                encrypted_before_ciphertext, encrypted_after_ciphertext, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000301',
                '00000000-0000-0000-0000-000000000302',
                'expired-before', 'expired-after', now() - interval '1 day');

            INSERT INTO entitysync.audit_events (
                tenant_id, audit_event_id, occurred_at, event_type, actor_id, correlation_id,
                redacted_values, redacted_values_sha256, full_values_sha256,
                full_values_expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000010', now() - interval '2 days',
                'RetentionTest', 'tester', 'retention-1', '{}', repeat('c', 64),
                repeat('d', 64), now() - interval '1 day');

            INSERT INTO entitysync.audit_event_full_values (
                tenant_id, audit_event_id, full_values_ciphertext, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000010',
                'expired-ciphertext', now() - interval '1 day');

            UPDATE entitysync.sync_operation_item_snapshots
            SET encrypted_before_ciphertext = NULL,
                encrypted_after_ciphertext = NULL,
                values_redacted_at = clock_timestamp()
            WHERE tenant_id = 'tenant-a'
              AND operation_id = '00000000-0000-0000-0000-000000000301'
              AND item_id = '00000000-0000-0000-0000-000000000302';

            UPDATE entitysync.audit_event_full_values
            SET full_values_ciphertext = NULL,
                values_redacted_at = clock_timestamp()
            WHERE tenant_id = 'tenant-a'
              AND audit_event_id = '00000000-0000-0000-0000-000000000010';
            """);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(1, await CountAsync("entitysync.sync_operation_items"));
        Assert.Equal(1, await CountAsync("entitysync.sync_operation_item_snapshots"));
        Assert.Equal(1, await CountAsync("entitysync.audit_events"));
        Assert.Equal(1, await CountAsync("entitysync.audit_event_full_values"));
    }

    [Fact]
    public async Task Snapshot_enrichment_is_fill_once_and_requires_unexpired_database_time()
    {
        await MigrateAsync();
        await SeedPlansAndApprovalsAsync();
        await using (var seed = Database.CreateCommand("""
                         INSERT INTO entitysync.sync_operations (
                             tenant_id, operation_id, plan_id, route_scope, mode, status,
                             idempotency_key, source_connection_generation,
                             target_connection_generation, attempt, created_at, queued_at)
                         VALUES
                             ('tenant-a', '00000000-0000-0000-0000-000000000321',
                              '00000000-0000-0000-0000-000000000101', 'route-a',
                              'DryRun', 'Succeeded', 'live-enrichment', 7, 11, 1,
                              clock_timestamp(), clock_timestamp()),
                             ('tenant-a', '00000000-0000-0000-0000-000000000322',
                              '00000000-0000-0000-0000-000000000101', 'route-a',
                              'DryRun', 'Succeeded', 'expired-enrichment', 7, 11, 1,
                              clock_timestamp(), clock_timestamp());

                         INSERT INTO entitysync.sync_operation_items (
                             tenant_id, operation_id, plan_id, item_id, source_vendor,
                             source_connection_id, source_entity_type, source_entity_key,
                             source_entity_id, target_vendor, target_connection_id,
                             target_entity_type, target_entity_id, action, redacted_before,
                             redacted_desired, desired_payload_sha256, snapshots_expires_at,
                             outcome, completed_at)
                         VALUES
                             ('tenant-a', '00000000-0000-0000-0000-000000000321',
                              '00000000-0000-0000-0000-000000000101',
                              '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                              'company', 'entity-1', 'ENTITY-1', 'target', 'target-1',
                              'account', 'TARGET-1', 'Update', '{}', '{}', repeat('3', 64),
                              now() + interval '1 day', 'Succeeded', clock_timestamp()),
                             ('tenant-a', '00000000-0000-0000-0000-000000000322',
                              '00000000-0000-0000-0000-000000000101',
                              '00000000-0000-0000-0000-000000000302', 'source', 'source-1',
                              'company', 'entity-1', 'ENTITY-1', 'target', 'target-1',
                              'account', 'TARGET-1', 'Update', '{}', '{}', repeat('3', 64),
                              now() - interval '1 day', 'Succeeded', clock_timestamp());

                         INSERT INTO entitysync.sync_operation_item_snapshots (
                             tenant_id, operation_id, item_id, encrypted_before_ciphertext,
                             encrypted_after_ciphertext, expires_at)
                         VALUES
                             ('tenant-a', '00000000-0000-0000-0000-000000000321',
                              '00000000-0000-0000-0000-000000000302',
                              'live-before', NULL, now() + interval '1 day'),
                             ('tenant-a', '00000000-0000-0000-0000-000000000322',
                              '00000000-0000-0000-0000-000000000302',
                              NULL, 'expired-after', now() - interval '1 day');
                         """))
            await seed.ExecuteNonQueryAsync();

        await using (var enrichLive = Database.CreateCommand("""
                         UPDATE entitysync.sync_operation_item_snapshots
                         SET encrypted_after_ciphertext = 'live-after'
                         WHERE tenant_id = 'tenant-a'
                           AND operation_id = '00000000-0000-0000-0000-000000000321'
                         """))
            Assert.Equal(1, await enrichLive.ExecuteNonQueryAsync());

        await using var replaceLive = Database.CreateCommand("""
            UPDATE entitysync.sync_operation_item_snapshots
            SET encrypted_after_ciphertext = 'replacement'
            WHERE tenant_id = 'tenant-a'
              AND operation_id = '00000000-0000-0000-0000-000000000321'
            """);
        Assert.Equal(
            "55000",
            (await Assert.ThrowsAsync<PostgresException>(
                () => replaceLive.ExecuteNonQueryAsync())).SqlState);

        await using var enrichExpired = Database.CreateCommand("""
            UPDATE entitysync.sync_operation_item_snapshots
            SET encrypted_before_ciphertext = 'too-late'
            WHERE tenant_id = 'tenant-a'
              AND operation_id = '00000000-0000-0000-0000-000000000322'
            """);
        Assert.Equal(
            "55000",
            (await Assert.ThrowsAsync<PostgresException>(
                () => enrichExpired.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Adjacent_inspection_pages_collectively_cover_and_approve_a_plan()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await AddSecondPlanItemAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000210',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000210',
                 '00000000-0000-0000-0000-000000000211', 0, 0, now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000210',
                 '00000000-0000-0000-0000-000000000212', 1, 1, now());

            UPDATE entitysync.sync_plan_inspections
            SET status = 'Completed', completed_at = now()
            WHERE tenant_id = 'tenant-a'
              AND inspection_id = '00000000-0000-0000-0000-000000000210';

            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000210',
                '00000000-0000-0000-0000-000000000210',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, now(), 'tester');
            """);
        Assert.Equal(5, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Inspection_completion_rejects_missing_overlapping_and_out_of_range_pages()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await AddSecondPlanItemAsync();
        await using (var seed = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000220',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                 7, 11, 'Open', now(), 'tester'),
                ('tenant-a', '00000000-0000-0000-0000-000000000221',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                 7, 11, 'Open', now(), 'tester'),
                ('tenant-a', '00000000-0000-0000-0000-000000000222',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                 7, 11, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000220',
                 '00000000-0000-0000-0000-000000000230', 0, 0, now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000221',
                 '00000000-0000-0000-0000-000000000231', 0, 1, now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000221',
                 '00000000-0000-0000-0000-000000000232', 1, 1, now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000222',
                 '00000000-0000-0000-0000-000000000233', 0, 2, now());
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        foreach (var inspectionId in new[] { 220, 221, 222 })
        {
            await using var complete = Database.CreateCommand($"""
                UPDATE entitysync.sync_plan_inspections
                SET status = 'Completed', completed_at = now()
                WHERE tenant_id = 'tenant-a'
                  AND inspection_id = '00000000-0000-0000-0000-000000000{inspectionId}';
                """);
            var error = await Assert.ThrowsAsync<PostgresException>(() => complete.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }
    }

    [Fact]
    public async Task Synthetic_full_range_requires_its_completed_inspection_session()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await AddSecondPlanItemAsync();

        await using var orphan = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000240',
                '00000000-0000-0000-0000-000000000241', 0, 1, now());
            """);
        var orphanError = await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync());
        Assert.Equal("23503", orphanError.SqlState);

        await using (var openSession = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000240',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000240',
                '00000000-0000-0000-0000-000000000241', 0, 1, now());
            """))
        {
            await openSession.ExecuteNonQueryAsync();
        }

        await using var approval = Database.CreateCommand("""
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000240',
                '00000000-0000-0000-0000-000000000240',
                '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                7, 11, now(), 'tester');
            """);
        var approvalError = await Assert.ThrowsAsync<PostgresException>(() => approval.ExecuteNonQueryAsync());
        Assert.Equal("55000", approvalError.SqlState);
    }

    [Fact]
    public async Task Plans_persist_connection_ids_and_coexisting_source_bounds_immutably()
    {
        await MigrateAsync();
        await SeedConnectionPolicyAsync();
        await using (var insert = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                source_search, source_count, source_entity_id,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                'source-1', 'target-1', 7, 11, 'acme', 25, 'ENTITY-1',
                repeat('1', 64), 'Draft', now(), 'tester', now() + interval '1 day');
            """))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await using var read = Database.CreateCommand("""
            SELECT source_connection_id, target_connection_id, source_search, source_count, source_entity_id
            FROM entitysync.sync_plans
            WHERE tenant_id = 'tenant-a'
              AND plan_id = '00000000-0000-0000-0000-000000000101';
            """);
        await using (var reader = await read.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("source-1", reader.GetString(0));
            Assert.Equal("target-1", reader.GetString(1));
            Assert.Equal("acme", reader.GetString(2));
            Assert.Equal(25, reader.GetInt32(3));
            Assert.Equal("ENTITY-1", reader.GetString(4));
        }

        await using var mutate = Database.CreateCommand("""
            UPDATE entitysync.sync_plans
            SET source_connection_id = 'source-2', target_connection_id = 'target-2',
                source_search = 'changed', source_count = 50, source_entity_id = 'ENTITY-2'
            WHERE tenant_id = 'tenant-a'
              AND plan_id = '00000000-0000-0000-0000-000000000101';
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task Plans_reject_nonpositive_counts_and_unbound_connection_generations()
    {
        await MigrateAsync();
        await SeedConnectionPolicyAsync();
        await using var wrongGeneration = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                'source-1', 'target-1', 8, 11,
                repeat('1', 64), 'Draft', now(), 'tester', now() + interval '1 day');
            """);
        var generationError =
            await Assert.ThrowsAsync<PostgresException>(() => wrongGeneration.ExecuteNonQueryAsync());
        Assert.Equal("23503", generationError.SqlState);

        await using var zeroCount = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                source_search, source_count,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000102',
                '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                'source-1', 'target-1', 7, 11, 'acme', 0,
                repeat('2', 64), 'Draft', now(), 'tester', now() + interval '1 day');
            """);
        var countError = await Assert.ThrowsAsync<PostgresException>(() => zeroCount.ExecuteNonQueryAsync());
        Assert.Equal("23514", countError.SqlState);

        await using var countWithoutSearch = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                source_count, plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000103',
                '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                'source-1', 'target-1', 7, 11, 5, repeat('3', 64),
                'Draft', now(), 'tester', now() + interval '1 day')
            RETURNING source_count;
            """);
        Assert.Equal(5, await countWithoutSearch.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Plan_item_match_details_require_ordered_arrays_and_are_immutable()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        await using (var valid = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                match_score, match_type, match_reasons, field_diffs,
                redacted_before, redacted_desired, desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000305', 1, 'source', 'source-1',
                'company', 'entity-2', 'ENTITY-2', 'target', 'target-1', 'account',
                'TARGET-2', 'Update', 87, 'Fuzzy',
                '["name similarity","domain match"]',
                '[{"fieldName":"name","before":"Acme","desired":"ACME"}]',
                '{}', '{}', repeat('5', 64));
            """))
        {
            await valid.ExecuteNonQueryAsync();
        }

        await using (var read = Database.CreateCommand("""
            SELECT match_reasons ->> 0, match_reasons ->> 1, field_diffs -> 0 ->> 'fieldName'
            FROM entitysync.sync_plan_items
            WHERE tenant_id = 'tenant-a'
              AND plan_id = '00000000-0000-0000-0000-000000000101'
              AND item_id = '00000000-0000-0000-0000-000000000305';
            """))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("name similarity", reader.GetString(0));
            Assert.Equal("domain match", reader.GetString(1));
            Assert.Equal("name", reader.GetString(2));
        }

        await using var invalidScore = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, action, match_score, match_type,
                match_reasons, field_diffs, redacted_before, redacted_desired,
                desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000306', 2, 'source', 'source-1',
                'company', 'entity-3', 'ENTITY-3', 'target', 'target-1', 'account',
                'Create', 101, 'Fuzzy', '[]', '[]', '{}', '{}', repeat('6', 64));
            """);
        var scoreError = await Assert.ThrowsAsync<PostgresException>(() => invalidScore.ExecuteNonQueryAsync());
        Assert.Equal("23514", scoreError.SqlState);

        await using var invalidShape = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, action, match_score, match_type,
                match_reasons, field_diffs, redacted_before, redacted_desired,
                desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000307', 3, 'source', 'source-1',
                'company', 'entity-4', 'ENTITY-4', 'target', 'target-1', 'account',
                'Create', 50, 'Fuzzy', '{"reason":"unordered"}', '[]',
                '{}', '{}', repeat('7', 64));
            """);
        var shapeError = await Assert.ThrowsAsync<PostgresException>(() => invalidShape.ExecuteNonQueryAsync());
        Assert.Equal("23514", shapeError.SqlState);

        await using var invalidDiffs = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, action, match_score, match_type,
                match_reasons, field_diffs, redacted_before, redacted_desired,
                desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000308', 4, 'source', 'source-1',
                'company', 'entity-5', 'ENTITY-5', 'target', 'target-1', 'account',
                'Create', 50, 'Fuzzy', '[]', '{"field":"unordered"}',
                '{}', '{}', repeat('8', 64));
            """);
        var diffsError = await Assert.ThrowsAsync<PostgresException>(() => invalidDiffs.ExecuteNonQueryAsync());
        Assert.Equal("23514", diffsError.SqlState);

        await using var invalidConnection = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, action, match_score, match_type,
                match_reasons, field_diffs, redacted_before, redacted_desired,
                desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000309', 5, 'source', 'source-2',
                'company', 'entity-6', 'ENTITY-6', 'target', 'target-1', 'account',
                'Create', 50, 'Fuzzy', '[]', '[]', '{}', '{}', repeat('9', 64));
            """);
        var connectionError =
            await Assert.ThrowsAsync<PostgresException>(() => invalidConnection.ExecuteNonQueryAsync());
        Assert.Equal("23514", connectionError.SqlState);

        await using var mutate = Database.CreateCommand("""
            UPDATE entitysync.sync_plan_items
            SET match_score = 99, match_type = 'Changed',
                match_reasons = '["changed"]', field_diffs = '[]'
            WHERE tenant_id = 'tenant-a'
              AND plan_id = '00000000-0000-0000-0000-000000000101'
              AND item_id = '00000000-0000-0000-0000-000000000305';
            """);
        var immutableError = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
        Assert.Equal("55000", immutableError.SqlState);
    }

    [Fact]
    public async Task Plan_item_arrays_reject_blank_malformed_and_duplicate_elements()
    {
        await MigrateAsync();
        await SeedPlansAsync();
        var invalidShapes = new[]
        {
            (Reasons: "[null]", Diffs: "[]"),
            (Reasons: "[\" \"]", Diffs: "[]"),
            (Reasons: "[1]", Diffs: "[]"),
            (Reasons: "[]", Diffs: "[null]"),
            (Reasons: "[]", Diffs: "[{\"fieldName\":\"name\",\"before\":{}}]"),
            (Reasons: "[]", Diffs: "[{\"fieldName\":\"name\",\"before\":{},\"desired\":{},\"extra\":1}]"),
            (Reasons: "[]", Diffs: "[{\"fieldName\":1,\"before\":{},\"desired\":{}}]"),
            (Reasons: "[]", Diffs: "[{\"fieldName\":\" \",\"before\":{},\"desired\":{}}]"),
            (Reasons: "[]", Diffs:
                "[{\"fieldName\":\"Name\",\"before\":{},\"desired\":{}},{\"fieldName\":\"name\",\"before\":{},\"desired\":{}}]")
        };

        for (var index = 0; index < invalidShapes.Length; index++)
        {
            await AssertPlanItemJsonRejectedAsync(
                Guid.Parse($"00000000-0000-0000-0000-{400 + index:D12}"),
                index + 10,
                invalidShapes[index].Reasons,
                invalidShapes[index].Diffs);
        }
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

    private async Task AssertPlanItemJsonRejectedAsync(
        Guid itemId,
        int itemOrdinal,
        string matchReasons,
        string fieldDiffs)
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, action, match_score, match_type,
                match_reasons, field_diffs, redacted_before, redacted_desired,
                desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                @item_id, @item_ordinal, 'source', 'source-1', 'company',
                @source_entity_key, @source_entity_id, 'target', 'target-1', 'account',
                'Create', 50, 'Fuzzy', @match_reasons, @field_diffs,
                '{}', '{}', repeat('a', 64));
            """);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("item_ordinal", itemOrdinal);
        command.Parameters.AddWithValue("source_entity_key", $"entity-{itemOrdinal}");
        command.Parameters.AddWithValue("source_entity_id", $"ENTITY-{itemOrdinal}");
        command.Parameters.AddWithValue(
            "match_reasons",
            NpgsqlTypes.NpgsqlDbType.Jsonb,
            matchReasons);
        command.Parameters.AddWithValue(
            "field_diffs",
            NpgsqlTypes.NpgsqlDbType.Jsonb,
            fieldDiffs);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
    }

    private async Task SeedConnectionPolicyAsync()
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.connection_definitions (
                tenant_id, connection_id, vendor, display_name, generation, enabled,
                public_configuration, secret_ciphertext, created_at, created_by, updated_at, updated_by)
            VALUES
                ('tenant-a', 'source-1', 'source', 'Source 1', 7, true,
                 '{}', 'source-secret-1', now(), 'tester', now(), 'tester'),
                ('tenant-a', 'target-1', 'target', 'Target 1', 11, true,
                 '{}', 'target-secret-1', now(), 'tester', now(), 'tester'),
                ('tenant-a', 'source-2', 'source', 'Source 2', 13, true,
                 '{}', 'source-secret-2', now(), 'tester', now(), 'tester'),
                ('tenant-a', 'target-2', 'target', 'Target 2', 17, true,
                 '{}', 'target-secret-2', now(), 'tester', now(), 'tester');

            INSERT INTO entitysync.sync_policies (
                tenant_id, policy_id, version, name, route_scope, definition,
                definition_sha256, enabled, created_at, created_by)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000100', 1, 'policy-a',
                'route-a', '{}', repeat('0', 64), true, now(), 'tester');
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedPlansAsync()
    {
        await SeedConnectionPolicyAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id,
                source_connection_generation, target_connection_generation,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000101',
                 '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                 'source-1', 'target-1', 7, 11,
                 repeat('1', 64), 'Draft', now(), 'tester', now() + interval '1 day'),
                ('tenant-a', '00000000-0000-0000-0000-000000000102',
                 '00000000-0000-0000-0000-000000000100', 1, 'route-a',
                 'source-2', 'target-2', 13, 17,
                 repeat('2', 64), 'Draft', now(), 'tester', now() + interval '1 day');

            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                match_score, match_type, match_reasons, field_diffs,
                redacted_before, redacted_desired, desired_payload_sha256)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000101',
                 '00000000-0000-0000-0000-000000000302', 0, 'source', 'source-1',
                 'company', 'entity-1', 'ENTITY-1', 'target', 'target-1', 'account',
                 'TARGET-1', 'Update', 100, 'Exact', '[]', '[]',
                 '{}', '{}', repeat('3', 64)),
                ('tenant-a', '00000000-0000-0000-0000-000000000102',
                 '00000000-0000-0000-0000-000000000303', 0, 'source', 'source-2',
                 'company', 'entity-2', 'ENTITY-2', 'target', 'target-2', 'account',
                 'TARGET-2', 'Update', 100, 'Exact', '[]', '[]',
                 '{}', '{}', repeat('4', 64));
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AddSecondPlanItemAsync()
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor, source_connection_id,
                source_entity_type, source_entity_key, source_entity_id, target_vendor,
                target_connection_id, target_entity_type, target_entity_id, action,
                match_score, match_type, match_reasons, field_diffs,
                redacted_before, redacted_desired, desired_payload_sha256)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000101',
                '00000000-0000-0000-0000-000000000304', 1, 'source', 'source-1',
                'company', 'entity-2', 'ENTITY-2', 'target', 'target-1', 'account',
                'TARGET-2', 'Update', 80, 'Fuzzy', '[]', '[]',
                '{}', '{}', repeat('4', 64));
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedPlansAndApprovalsAsync()
    {
        await SeedPlansAsync();
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000201',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                 7, 11, 'Open', now(), 'tester'),
                ('tenant-a', '00000000-0000-0000-0000-000000000202',
                 '00000000-0000-0000-0000-000000000102', repeat('2', 64),
                 13, 17, 'Open', now(), 'tester');

            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000201',
                 '00000000-0000-0000-0000-000000000211', 0, 0, now()),
                ('tenant-a', '00000000-0000-0000-0000-000000000202',
                 '00000000-0000-0000-0000-000000000212', 0, 0, now());

            UPDATE entitysync.sync_plan_inspections
            SET status = 'Completed', completed_at = now()
            WHERE tenant_id = 'tenant-a';

            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by)
            VALUES
                ('tenant-a', '00000000-0000-0000-0000-000000000111',
                 '00000000-0000-0000-0000-000000000201',
                 '00000000-0000-0000-0000-000000000101', repeat('1', 64),
                 7, 11, now(), 'tester'),
                ('tenant-a', '00000000-0000-0000-0000-000000000112',
                 '00000000-0000-0000-0000-000000000202',
                 '00000000-0000-0000-0000-000000000102', repeat('2', 64),
                 13, 17, now(), 'tester');
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
                'target', 'target-1', 'account', 'existing-entity', 'EXISTING-ENTITY',
                'Existing entity', 'migration preservation', 'test');

            INSERT INTO entitysync.entity_change_state (
                tenant_id, route_scope, source_vendor, source_connection_id, source_entity_type,
                target_vendor, target_connection_id, target_entity_type, source_entity_key,
                source_entity_id, source_name, target_entity_id, hash_version, payload_hash, applied_at)
            VALUES (
                'tenant-a', repeat('a', 64), 'source', 'source-1', 'company',
                'target', 'target-1', 'account', 'existing-entity', 'EXISTING-ENTITY',
                'Existing entity', 'TARGET-1', 1, repeat('b', 64), now());
            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertAuditMutationRejectedAsync()
    {
        await using var insert = Database.CreateCommand("""
            INSERT INTO entitysync.audit_events (
                tenant_id, audit_event_id, occurred_at, event_type, actor_id, operation_id,
                run_id, plan_id, item_id, correlation_id, redacted_values,
                redacted_values_sha256, full_values_sha256, full_values_expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000010', now(), 'OperationQueued',
                'tester', '00000000-0000-0000-0000-000000000020',
                '00000000-0000-0000-0000-000000000030',
                '00000000-0000-0000-0000-000000000040',
                '00000000-0000-0000-0000-000000000050', 'correlation-1',
                '{"secret":"[redacted]"}', repeat('c', 64), repeat('d', 64),
                now() + interval '365 days');

            INSERT INTO entitysync.audit_event_full_values (
                tenant_id, audit_event_id, full_values_ciphertext, expires_at)
            VALUES (
                'tenant-a', '00000000-0000-0000-0000-000000000010',
                'ciphertext', now() + interval '365 days');
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

    private async Task SetMigrationVersionAsync(string version, bool present)
    {
        await using var command = Database.CreateCommand(
            present
                ? "INSERT INTO entitysync.schema_migrations (version) VALUES (@version)"
                : "DELETE FROM entitysync.schema_migrations WHERE version = @version");
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync();
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
