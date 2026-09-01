using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;
using LISSTech.EntitySync.Scheduler;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlSchedulerTestsPostgres : IAsyncLifetime
{
    private readonly string databaseName = $"entitysync_scheduler_{Guid.NewGuid():N}";
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource? database;

    [Fact]
    public async Task Canonical_replay_keeps_original_policy_work_links()
    {
        const string tenant = "canonical-replay";
        var policies = new PostgresSyncPolicyRepository(Database);
        var firstPolicy = Policy(tenant);
        await policies.InsertAsync(tenant, firstPolicy, default);
        var queue = new PostgresSyncWorkQueue(Database);
        var request = CanonicalRequest(tenant, "om-42");

        var first = await queue.AcceptAsync(request, DateTimeOffset.UtcNow, default);
        await policies.InsertAsync(tenant, firstPolicy.NextVersion(
            new EntitySyncActor("admin"), firstPolicy.Definition,
            DateTimeOffset.UtcNow.AddSeconds(1)), default);
        var replay = await queue.AcceptAsync(request, DateTimeOffset.UtcNow, default);

        Assert.Equal(first.ReceiptId, replay.ReceiptId);
        Assert.Equal(first.WorkIds, replay.WorkIds);
        Assert.Single(replay.WorkIds);
    }

    [Fact]
    public async Task Legacy_canonical_replay_returns_stored_receipt_and_work_links()
    {
        const string tenant = "legacy-replay";
        var policy = Policy(tenant);
        await new PostgresSyncPolicyRepository(Database).InsertAsync(tenant, policy, default);
        var request = CanonicalRequest(tenant, "legacy-om");
        var eventId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        await using (var seed = Database.CreateCommand("""
            INSERT INTO entitysync.canonical_change_events (
                tenant_id, event_id, receipt_id, om_event_id, canonical_entity_type,
                canonical_entity_id, canonical_version, changed_fields, payload_sha256,
                occurred_at, received_at, status)
            VALUES (@tenant, @event, @receipt, @outbox, @type, @entity::text, @version,
                    @fields, @hash, @occurred, clock_timestamp(), 'Planned');
            INSERT INTO entitysync.sync_control_work (
                tenant_id, work_id, work_kind, state, policy_id, policy_version,
                route_scope, canonical_event_id, canonical_entity_type,
                canonical_entity_id, canonical_version, changed_fields, payload_sha256)
            VALUES (@tenant, @work, 'CanonicalChange', 'Queued', @policy, 1,
                    @route, @event, @type, @entity, @version, @fields, @hash);
            """))
        {
            seed.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            seed.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, eventId);
            seed.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receiptId);
            seed.Parameters.AddWithValue("outbox", NpgsqlDbType.Text, request.OutboxEventId);
            seed.Parameters.AddWithValue("type", NpgsqlDbType.Text, request.CanonicalEntityType);
            seed.Parameters.AddWithValue("entity", NpgsqlDbType.Uuid, request.CanonicalEntityId);
            seed.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, request.CanonicalVersion);
            seed.Parameters.AddWithValue("fields", NpgsqlDbType.Jsonb,
                System.Text.Json.JsonSerializer.Serialize(request.ChangedFields));
            seed.Parameters.AddWithValue("hash", NpgsqlDbType.Char, request.PayloadSha256.Value);
            seed.Parameters.AddWithValue("occurred", NpgsqlDbType.TimestampTz, request.OccurredAt);
            seed.Parameters.AddWithValue("work", NpgsqlDbType.Uuid, workId);
            seed.Parameters.AddWithValue("policy", NpgsqlDbType.Uuid, policy.PolicyId);
            seed.Parameters.AddWithValue("route", NpgsqlDbType.Text, policy.RouteScope);
            await seed.ExecuteNonQueryAsync();
        }

        var replay = await new PostgresSyncWorkQueue(Database).AcceptAsync(
            request, DateTimeOffset.UtcNow, default);

        Assert.Equal(receiptId, replay.ReceiptId);
        Assert.Equal([workId], replay.WorkIds);
    }

    [Fact]
    public async Task Work_checkpoints_survive_expired_claim_recovery()
    {
        const string tenant = "checkpoint-recovery";
        var policy = Policy(tenant);
        await new PostgresSyncPolicyRepository(Database).InsertAsync(tenant, policy, default);
        var workId = Guid.NewGuid();
        await InsertScheduleWorkAsync(tenant, workId, policy);
        var queue = new PostgresSyncWorkQueue(Database);
        var first = await queue.TryLeaseNextAsync(
            tenant, "owner-a", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        Assert.True(await queue.TryStartPlanningAsync(first, default));
        first = first with { State = SyncControlWorkState.Planning };
        var planId = Guid.NewGuid();
        var digest = new EntitySyncSha256(new string('a', 64));
        Assert.True(await queue.TryCheckpointPlanAsync(first, planId, digest, default));
        await ExpireWorkLeaseAsync(tenant, workId);

        var second = await queue.TryLeaseNextAsync(
            tenant, "owner-b", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(second);
        Assert.Equal(planId, second.PlanId);
        Assert.Equal(digest, second.PlanDigestSha256);
        Assert.True(await queue.TryStartPlanningAsync(second, default));
        second = second with { State = SyncControlWorkState.Planning };
        var approvalId = Guid.NewGuid();
        Assert.True(await queue.TryCheckpointApprovalAsync(second, approvalId, default));
        await ExpireWorkLeaseAsync(tenant, workId);

        var third = await queue.TryLeaseNextAsync(
            tenant, "owner-c", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(third);
        Assert.Equal(planId, third.PlanId);
        Assert.Equal(approvalId, third.ApprovalId);
        Assert.True(await queue.TryStartPlanningAsync(third, default));
        third = third with { State = SyncControlWorkState.Planning };
        var operationId = Guid.NewGuid();
        Assert.True(await queue.TryCheckpointOperationAsync(third, operationId, default));
        await ExpireWorkLeaseAsync(tenant, workId);

        var fourth = await queue.TryLeaseNextAsync(
            tenant, "owner-d", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(fourth);
        Assert.Equal(operationId, fourth.OperationId);
    }

    [Fact]
    public async Task Route_contention_defers_hot_work_and_leases_another_route()
    {
        const string tenant = "route-contention";
        var policy = Policy(tenant);
        await new PostgresSyncPolicyRepository(Database).InsertAsync(tenant, policy, default);
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await InsertScheduleWorkAsync(tenant, firstId, policy, "route-a", DateTimeOffset.UtcNow.AddMinutes(-2));
        await InsertScheduleWorkAsync(tenant, secondId, policy, "route-b", DateTimeOffset.UtcNow.AddMinutes(-1));
        var queue = new PostgresSyncWorkQueue(Database);

        var first = await queue.TryLeaseNextAsync(
            tenant, "owner", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        Assert.Equal(firstId, first.WorkId);
        Assert.True(await queue.TryDeferAsync(first, TimeSpan.FromSeconds(2), default));
        var next = await queue.TryLeaseNextAsync(
            tenant, "owner", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(next);

        Assert.Equal(secondId, next.WorkId);
    }

    [Fact]
    public async Task Operation_route_gate_fences_contenders_and_renews_with_database_clock()
    {
        var operation = EntitySyncOperation.QueueDryRun(
            "operation-route", Guid.NewGuid(), Guid.NewGuid(), "key", "shared-route",
            "source", 1, "target", 1, DateTimeOffset.UtcNow);
        var routeLock = new PostgresRouteLock(Database);
        var gate = (IEntitySyncOperationRouteLock)routeLock;
        await using var first = await gate.TryAcquireAsync(
            operation, "owner-a", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        await using var contender = await gate.TryAcquireAsync(
            operation, "owner-b", TimeSpan.FromMinutes(1), default);
        Assert.Null(contender);
        Assert.True(await first.TryRenewAsync(TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Operation_snapshot_scrub_preserves_identity_and_rejects_mutation()
    {
        const string tenant = "operation-retention";
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var seed = Database.CreateCommand("""
            SET session_replication_role = replica;
            INSERT INTO entitysync.sync_operation_item_snapshots (
                tenant_id, operation_id, item_id, encrypted_before_ciphertext,
                encrypted_after_ciphertext, expires_at)
            VALUES (@tenant, @operation, @item, 'before', 'after',
                    clock_timestamp() - interval '1 day');
            SET session_replication_role = origin;
            """))
        {
            seed.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            seed.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            seed.Parameters.AddWithValue("item", NpgsqlDbType.Uuid, itemId);
            await seed.ExecuteNonQueryAsync();
        }
        await using (var mutate = Database.CreateCommand("""
            UPDATE entitysync.sync_operation_item_snapshots
            SET item_id = @replacement,
                encrypted_before_ciphertext = NULL,
                encrypted_after_ciphertext = NULL,
                values_redacted_at = clock_timestamp()
            WHERE tenant_id = @tenant AND operation_id = @operation AND item_id = @item
            """))
        {
            mutate.Parameters.AddWithValue("replacement", NpgsqlDbType.Uuid, Guid.NewGuid());
            mutate.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            mutate.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            mutate.Parameters.AddWithValue("item", NpgsqlDbType.Uuid, itemId);
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => mutate.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }
        var repository = new PostgresSyncOperationRepository(Database);
        Assert.Equal(1, await repository.DeleteExpiredSnapshotsAsync(
            tenant, DateTimeOffset.MaxValue, 10, default));
        Assert.Null(await repository.GetSnapshotAsync(
            tenant, operationId, itemId, default));
        await using var metadata = Database.CreateCommand("""
            SELECT operation_id, item_id, expires_at, values_redacted_at
            FROM entitysync.sync_operation_item_snapshots
            WHERE tenant_id = @tenant AND operation_id = @operation AND item_id = @item
            """);
        metadata.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
        metadata.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        metadata.Parameters.AddWithValue("item", NpgsqlDbType.Uuid, itemId);
        await using var reader = await metadata.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(operationId, reader.GetGuid(0));
        Assert.Equal(itemId, reader.GetGuid(1));
        Assert.False(reader.IsDBNull(3));
    }

    [Fact]
    public async Task Operation_lease_renewal_is_database_clock_and_attempt_fenced()
    {
        const string tenant = "operation-renewal";
        var operationId = Guid.NewGuid();
        await using (var seed = Database.CreateCommand("""
            SET session_replication_role = replica;
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, route_scope,
                source_connection_generation, target_connection_generation,
                mode, status, idempotency_key, lease_owner, lease_expires_at,
                attempt, created_at, queued_at, started_at)
            VALUES (@tenant, @operation, @plan, 'route', 1, 1, 'DryRun', 'Running',
                    'renewal-key', 'owner-a', clock_timestamp() + interval '2 seconds',
                    1, clock_timestamp(), clock_timestamp(), clock_timestamp());
            SET session_replication_role = origin;
            """))
        {
            seed.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            seed.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            seed.Parameters.AddWithValue("plan", NpgsqlDbType.Uuid, Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }
        var repository = new PostgresSyncOperationRepository(Database);
        Assert.True(await repository.TryRenewLeaseAsync(
            tenant, operationId, 1, "owner-a", TimeSpan.FromMinutes(1), default));
        await using (var steal = Database.CreateCommand("""
            SET session_replication_role = replica;
            UPDATE entitysync.sync_operations
            SET lease_owner = 'owner-b', attempt = 2,
                lease_expires_at = clock_timestamp() + interval '1 minute'
            WHERE tenant_id = @tenant AND operation_id = @operation;
            SET session_replication_role = origin;
            """))
        {
            steal.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            steal.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            await steal.ExecuteNonQueryAsync();
        }
        Assert.False(await repository.TryRenewLeaseAsync(
            tenant, operationId, 1, "owner-a", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Retention_scrub_reads_unavailable_and_rejects_identity_mutation()
    {
        const string tenant = "retention";
        var repository = new PostgresSyncAuditRepository(Database);
        var expiry = DateTimeOffset.UtcNow.AddDays(-1);
        var occurred = expiry.AddDays(-1);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await repository.AppendAsync(tenant, Audit(tenant, firstId, occurred, expiry),
            new EntitySyncAuditEventFullValues(tenant, firstId, "ciphertext", expiry), default);
        await repository.AppendAsync(tenant, Audit(tenant, secondId, occurred, expiry),
            new EntitySyncAuditEventFullValues(tenant, secondId, "ciphertext-2", expiry), default);

        await using (var mutate = Database.CreateCommand("""
            UPDATE entitysync.audit_event_full_values
            SET audit_event_id = @replacement,
                full_values_ciphertext = NULL,
                values_redacted_at = clock_timestamp()
            WHERE tenant_id = @tenant AND audit_event_id = @original
            """))
        {
            mutate.Parameters.AddWithValue("replacement", NpgsqlDbType.Uuid, secondId);
            mutate.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
            mutate.Parameters.AddWithValue("original", NpgsqlDbType.Uuid, firstId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }

        Assert.Equal(2, await repository.DeleteExpiredFullValuesAsync(
            tenant, DateTimeOffset.MaxValue, 10, default));
        Assert.Null(await repository.GetFullValuesAsync(tenant, firstId, default));
        await using var metadata = Database.CreateCommand("""
            SELECT full_values_sha256, full_values_expires_at, values_redacted_at
            FROM entitysync.audit_events
            WHERE tenant_id = @tenant AND audit_event_id = @event
            """);
        metadata.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
        metadata.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, firstId);
        await using var reader = await metadata.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(expiry, reader.GetFieldValue<DateTimeOffset>(1));
        Assert.False(reader.IsDBNull(2));
    }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? $"Host=127.0.0.1;Port=5433;Database=postgres;Username=postgres;Pooling=false";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        admin = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE DATABASE \"{databaseName}\""))
            await create.ExecuteNonQueryAsync();
        var databaseBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = true,
            MaxPoolSize = 8
        };
        database = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
        await EntitySyncDatabaseMigrator.ApplyAsync(Database);
    }

    public async Task DisposeAsync()
    {
        if (database is not null) await database.DisposeAsync();
        if (admin is not null)
        {
            await using (var drop = admin.CreateCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)"))
                await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private NpgsqlDataSource Database => database ?? throw new InvalidOperationException();

    private static EntitySyncPolicy Policy(string tenant)
    {
        var definition = new EntitySyncPolicyDefinition(
            "OrchestraMSP", "source", "Client", "HaloPSA", "target", "Client",
            false, false, 90, 70, null, null,
            EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
            ["Name"], ["Secret"], true);
        return EntitySyncPolicy.Create(tenant, Guid.NewGuid(), "policy", "route",
            definition, true, DateTimeOffset.UtcNow, new EntitySyncActor("admin"));
    }

    private static CanonicalChangeRequest CanonicalRequest(string tenant, string eventId) =>
        new(tenant, eventId, "Client",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 7,
            ["Name"], new EntitySyncSha256(new string('b', 64)), DateTimeOffset.UtcNow);

    private static EntitySyncAuditEvent Audit(
        string tenant, Guid eventId, DateTimeOffset occurred, DateTimeOffset expiry)
    {
        var values = new EntitySyncJsonValue("{}");
        return new EntitySyncAuditEvent(tenant, eventId, occurred, "Test",
            new EntitySyncActor("test"), null, null, null, null, eventId.ToString("N"),
            values, EntitySyncCanonicalDigest.Compute(new { Redacted = true }),
            new EntitySyncSha256(new string('c', 64)), expiry);
    }

    private async Task InsertScheduleWorkAsync(
        string tenant, Guid workId, EntitySyncPolicy policy,
        string route = "route", DateTimeOffset? createdAt = null)
    {
        await using var command = Database.CreateCommand("""
            INSERT INTO entitysync.sync_control_work (
                tenant_id, work_id, work_kind, state, policy_id, policy_version,
                route_scope, schedule_id, schedule_version, scheduled_for,
                created_at, updated_at)
            VALUES (@tenant, @work, 'Schedule', 'Queued', @policy, @version,
                    @route, @schedule, 1, clock_timestamp(), @created, @created)
            """);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
        command.Parameters.AddWithValue("work", NpgsqlDbType.Uuid, workId);
        command.Parameters.AddWithValue("policy", NpgsqlDbType.Uuid, policy.PolicyId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, policy.Version);
        command.Parameters.AddWithValue("route", NpgsqlDbType.Text, route);
        command.Parameters.AddWithValue("schedule", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz,
            createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExpireWorkLeaseAsync(string tenant, Guid workId)
    {
        await using var command = Database.CreateCommand("""
            UPDATE entitysync.sync_control_work
            SET lease_expires_at = clock_timestamp() - interval '1 second'
            WHERE tenant_id = @tenant AND work_id = @work
            """);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant);
        command.Parameters.AddWithValue("work", NpgsqlDbType.Uuid, workId);
        await command.ExecuteNonQueryAsync();
    }
}
