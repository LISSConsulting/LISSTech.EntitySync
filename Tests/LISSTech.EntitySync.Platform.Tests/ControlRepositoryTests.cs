using System.Security.Cryptography;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlRepositoryTests : IAsyncLifetime
{
    private readonly string databaseName = $"entitysync_control_repo_{Guid.NewGuid():N}";
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource? database;

    [Fact]
    public void Data_protection_isolated_by_purpose_application_and_key_ring()
    {
        using var keyRingA = new TemporaryDirectory();
        using var keyRingB = new TemporaryDirectory();
        var protectorA = CreateProtector(keyRingA.Path, "application-a");
        var sameApplication = CreateProtector(keyRingA.Path, "application-a");
        var otherApplication = CreateProtector(keyRingA.Path, "application-b");
        var otherKeyRing = CreateProtector(keyRingB.Path, "application-a");

        const string secret = "vendor-secret-that-must-never-be-plaintext";
        var ciphertext = protectorA.Protect(EntitySyncDataProtectionPurpose.ConnectionSecret, secret);

        Assert.DoesNotContain(secret, ciphertext, StringComparison.Ordinal);
        Assert.Equal(secret, sameApplication.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            protectorA.Unprotect(EntitySyncDataProtectionPurpose.AuditValue, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            otherApplication.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            otherKeyRing.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
    }

    [Fact]
    public void Hosting_resolves_control_repositories_with_external_data_protection()
    {
        using var keyRing = new TemporaryDirectory();
        var original = Environment.GetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", null);
            Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddEntitySyncPlatform(
                    "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                    EntitySyncHostMode.Http));
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyRing.Path);
            var services = new ServiceCollection();
            services.AddEntitySyncPlatform(
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                EntitySyncHostMode.Http);
            using var provider = services.BuildServiceProvider();
            Assert.IsType<PostgresConnectionDefinitionRepository>(
                provider.GetRequiredService<IConnectionDefinitionRepository>());
            Assert.IsType<PostgresSyncPolicyRepository>(
                provider.GetRequiredService<ISyncPolicyRepository>());
            Assert.IsType<PostgresDurableSyncPlanRepository>(
                provider.GetRequiredService<IDurableSyncPlanRepository>());
            Assert.IsType<PostgresSyncOperationRepository>(
                provider.GetRequiredService<ISyncOperationRepository>());
            Assert.IsType<PostgresSyncScheduleRepository>(
                provider.GetRequiredService<ISyncScheduleRepository>());
            Assert.IsType<PostgresSyncAuditRepository>(
                provider.GetRequiredService<ISyncAuditRepository>());
            Assert.Same(
                provider.GetRequiredService<IIdempotencyRepository>(),
                provider.GetRequiredService<IIdempotentCommandExecutor>());
            var protector = provider.GetRequiredService<IEntitySyncDataProtector>();
            var ciphertext = protector.Protect(
                EntitySyncDataProtectionPurpose.ConnectionSecret, "host-secret");
            Assert.Equal(
                "host-secret",
                protector.Unprotect(
                    EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", original);
        }
    }

    [Fact]
    public async Task Connection_and_policy_repositories_are_tenant_isolated_and_lossless()
    {
        var connectionRepository = new PostgresConnectionDefinitionRepository(Database);
        var policyRepository = new PostgresSyncPolicyRepository(Database);
        var now = DateTimeOffset.UtcNow;
        using var keyRing = new TemporaryDirectory();
        var protector = CreateProtector(keyRing.Path, "repository-test");
        var secret = protector.Protect(EntitySyncDataProtectionPurpose.ConnectionSecret, "plain-secret");
        var connection = Connection("tenant-a", "source", "NetSuite", 1, secret, now);

        await connectionRepository.InsertAsync("tenant-a", connection, default);
        await using (var raw = Database.CreateCommand("""
            SELECT secret_ciphertext
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            """))
        {
            raw.Parameters.AddWithValue("tenant_id", connection.TenantId);
            raw.Parameters.AddWithValue("connection_id", connection.ConnectionId);
            var storedCiphertext = Assert.IsType<string>(await raw.ExecuteScalarAsync());
            Assert.DoesNotContain("plain-secret", storedCiphertext, StringComparison.Ordinal);
        }

        Assert.Equal(connection, await connectionRepository.GetAsync("tenant-a", "source", default));
        Assert.Null(await connectionRepository.GetAsync("tenant-b", "source", default));
        Assert.Single(await connectionRepository.ListAsync("tenant-a", "NetSuite", true, default));
        Assert.Empty(await connectionRepository.ListAsync("tenant-b", null, null, default));
        var replacement = connection.NextGeneration(
            "Source v2", false, new EntitySyncJsonValue("{\"region\":\"us\"}"), secret,
            new EntitySyncActor("updater"), now.AddMinutes(1));
        await Assert.ThrowsAsync<ArgumentException>(() => connectionRepository.TryReplaceAsync(
            "tenant-b", "source", 1, replacement, default));
        Assert.True(await connectionRepository.TryReplaceAsync(
            "tenant-a", "source", 1, replacement, default));
        Assert.False(await connectionRepository.TryReplaceAsync(
            "tenant-a", "source", 1, replacement, default));

        var policy = Policy("tenant-a", connection.ConnectionId, "target", now);
        await connectionRepository.InsertAsync(
            "tenant-a", Connection("tenant-a", "target", "HaloPSA", 1, secret, now), default);
        await policyRepository.InsertAsync("tenant-a", policy, default);

        var storedPolicy = await policyRepository.GetAsync("tenant-a", policy.PolicyId, 1, default);
        var latestPolicy = await policyRepository.GetLatestAsync("tenant-a", policy.PolicyId, default);
        Assert.NotNull(storedPolicy);
        Assert.NotNull(latestPolicy);
        Assert.Equal(policy.DefinitionSha256, storedPolicy.DefinitionSha256);
        Assert.Equal(policy.Definition.AllowedFields.Order(), storedPolicy.Definition.AllowedFields.Order());
        Assert.Equal(policy.DefinitionSha256, latestPolicy.DefinitionSha256);
        Assert.Null(await policyRepository.GetAsync("tenant-b", policy.PolicyId, 1, default));
        Assert.Single(await policyRepository.ListLatestAsync("tenant-a", "route-a", true, default));
    }

    [Fact]
    public async Task Manifest_round_trips_and_item_failure_rolls_back_plan()
    {
        var context = await SeedControlContextAsync("manifest");
        var repository = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 2);

        await repository.InsertAsync(context.TenantId, manifest, default);

        Assert.Equal(manifest.Plan, await repository.GetAsync(context.TenantId, manifest.Plan.PlanId, default));
        Assert.Null(await repository.GetAsync("other-tenant", manifest.Plan.PlanId, default));
        var page = await repository.GetPageAsync(context.TenantId, manifest.Plan.PlanId, 1, 10, default);
        Assert.Equal(
            manifest.Items.Select(item => (
                item.ItemId, item.ItemOrdinal, item.MatchEvidence.Score,
                item.MatchEvidence.MatchType, item.RedactedBefore.Json,
                item.RedactedDesired.Json, item.DesiredPayloadSha256.Value)),
            page.Items.Select(item => (
                item.ItemId, item.ItemOrdinal, item.MatchEvidence.Score,
                item.MatchEvidence.MatchType, item.RedactedBefore.Json,
                item.RedactedDesired.Json, item.DesiredPayloadSha256.Value)));
        Assert.Equal(
            manifest.Items.SelectMany(item => item.MatchEvidence.Reasons),
            page.Items.SelectMany(item => item.MatchEvidence.Reasons));
        Assert.Equal(
            manifest.Items.SelectMany(item => item.FieldDiffs)
                .Select(diff => (diff.FieldName, diff.Before.Json, diff.Desired.Json)),
            page.Items.SelectMany(item => item.FieldDiffs)
                .Select(diff => (diff.FieldName, diff.Before.Json, diff.Desired.Json)));
        Assert.Equal(2, page.TotalItems);

        var badContext = await SeedControlContextAsync("rollback");
        var badManifest = Manifest(badContext, itemCount: 2, reason: "bad\0reason");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            repository.InsertAsync(badContext.TenantId, badManifest, default));
        Assert.Null(await repository.GetAsync(badContext.TenantId, badManifest.Plan.PlanId, default));
    }

    [Fact]
    public async Task Inspection_requires_exact_coverage_and_approval_is_single_use_and_expiring()
    {
        var context = await SeedControlContextAsync("approval");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 2);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var inspectionId = Guid.NewGuid();
        var now = context.Now.AddMinutes(1);
        await plans.OpenInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await Assert.ThrowsAnyAsync<Exception>(() => plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            now.AddMinutes(1), default));
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 1, 1, now, default);
        var completed = await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            now.AddMinutes(1), default);
        Assert.Equal(EntitySyncInspectionStatus.Completed, completed.Status);
        Assert.True(await plans.HasCompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, default));

        var approvalId = Guid.NewGuid();
        var approval = await plans.ApproveInspectionAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("approver"),
            now.AddMinutes(2), now.AddMinutes(10), default);
        Assert.Equal(approvalId, approval.ApprovalId);
        Assert.Equal(EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);

        var operation = EntitySyncOperation.QueueApply(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, approvalId,
            "apply-once", "route-a", context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(3));
        var operationItems = OperationItems(operation, manifest.Items, now.AddDays(1));
        var operationTwo = EntitySyncOperation.QueueApply(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, approvalId,
            "apply-twice", "route-a", context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(3));
        var attempts = await Task.WhenAll(
            plans.TryConsumeApprovalAsync(
                context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
                context.Target.ConnectionId, 1, operation, operationItems,
                now.AddMinutes(3), default),
            plans.TryConsumeApprovalAsync(
                context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
                context.Target.ConnectionId, 1, operationTwo,
                OperationItems(operationTwo, manifest.Items, now.AddDays(1)),
                now.AddMinutes(3), default));
        Assert.Single(attempts, value => value);
        Assert.Equal(EntitySyncDurablePlanStatus.Consumed,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Expired_approval_cannot_be_consumed()
    {
        var context = await SeedControlContextAsync("expired-approval");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var now = context.Now.AddMinutes(1);
        var inspectionId = Guid.NewGuid();
        await plans.OpenInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(1), default);
        var approvalId = Guid.NewGuid();
        await plans.ApproveInspectionAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("approver"),
            now.AddMinutes(2), now.AddMinutes(3), default);
        var operation = EntitySyncOperation.QueueApply(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, approvalId,
            "expired-apply", "route-a", context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(4));

        Assert.False(await plans.TryConsumeApprovalAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, operation,
            OperationItems(operation, manifest.Items, now.AddDays(1)),
            now.AddMinutes(4), default));
        Assert.Equal(
            EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Lease_reclamation_and_item_compare_and_set_fence_stale_workers()
    {
        var context = await SeedControlContextAsync("lease");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var operations = new PostgresSyncOperationRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var operation = EntitySyncOperation.QueueDryRun(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, "dry-run",
            "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            context.Now.AddMinutes(1));
        var items = OperationItems(operation, manifest.Items, context.Now.AddDays(1));
        await operations.InsertAsync(context.TenantId, operation, items, default);

        var now = context.Now.AddMinutes(2);
        var firstRace = await Task.WhenAll(
            operations.TryLeaseNextAsync(context.TenantId, "worker-a", now, now.AddMinutes(1), default),
            operations.TryLeaseNextAsync(context.TenantId, "worker-b", now, now.AddMinutes(1), default));
        Assert.Single(firstRace, value => value is not null);
        var firstLease = firstRace.Single(value => value is not null)!;
        var reclaimed = await operations.TryLeaseNextAsync(
            context.TenantId, "worker-c", now.AddMinutes(2), now.AddMinutes(3), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal("worker-c", reclaimed.LeaseOwner);

        var completed = EntitySyncOperationItem.Rehydrate(
            items[0].TenantId, items[0].OperationId, items[0].PlanId, items[0].ItemId,
            items[0].SourceVendor, items[0].SourceConnectionId, items[0].SourceEntityType,
            items[0].SourceEntityKey, items[0].SourceEntityId, items[0].TargetVendor,
            items[0].TargetConnectionId, items[0].TargetEntityType, items[0].TargetEntityId,
            items[0].Action, items[0].RedactedBefore, items[0].RedactedDesired,
            items[0].BeforePayloadSha256, items[0].DesiredPayloadSha256,
            new EntitySyncSha256(new string('c', 64)), items[0].SnapshotsExpireAt,
            "request-1", EntitySyncItemOutcome.Succeeded, null, null,
            now.AddMinutes(2), now.AddMinutes(2));
        Assert.False(await operations.TryReplaceItemAsync(
            context.TenantId, operation.OperationId, operation.PlanId, items[0].ItemId,
            firstLease.Attempt, firstLease.LeaseOwner!, now.AddMinutes(2),
            EntitySyncItemOutcome.Pending, completed, default));
        Assert.True(await operations.TryReplaceItemAsync(
            context.TenantId, operation.OperationId, operation.PlanId, items[0].ItemId,
            reclaimed.Attempt, reclaimed.LeaseOwner!, now.AddMinutes(2),
            EntitySyncItemOutcome.Pending, completed, default));
    }

    [Fact]
    public async Task Schedule_audit_retention_and_plan_expiration_are_tenant_scoped()
    {
        var context = await SeedControlContextAsync("schedule");
        var schedules = new PostgresSyncScheduleRepository(Database);
        var schedule = new EntitySyncSchedule(
            context.TenantId, Guid.NewGuid(), 1, "nightly", context.Policy.PolicyId, 1,
            "0 0 * * *", "UTC", true, context.Now.AddHours(1), null,
            context.Now, new EntitySyncActor("scheduler"));
        await schedules.InsertVersionAsync(context.TenantId, schedule, default);
        Assert.Equal(schedule, await schedules.GetLatestAsync(context.TenantId, schedule.ScheduleId, default));
        Assert.Null(await schedules.GetAsync("other-tenant", schedule.ScheduleId, 1, default));
        Assert.Single(await schedules.ListDueAsync(
            context.TenantId, context.Now.AddHours(2), 10, default));
        var change = new EntitySyncCanonicalChangeEvent(
            context.TenantId, Guid.NewGuid(), "Company", "company-1", 1,
            new EntitySyncJsonValue("[\"name\"]"), context.Now, context.Now,
            EntitySyncCanonicalChangeStatus.Pending);
        await schedules.InsertChangeEventAsync(context.TenantId, change, default);
        Assert.True(await schedules.TrySetChangeEventStatusAsync(
            context.TenantId, change.EventId, EntitySyncCanonicalChangeStatus.Pending,
            EntitySyncCanonicalChangeStatus.Planned, default));
        Assert.False(await schedules.TrySetChangeEventStatusAsync(
            "other-tenant", change.EventId, EntitySyncCanonicalChangeStatus.Planned,
            EntitySyncCanonicalChangeStatus.Failed, default));

        using var keyRing = new TemporaryDirectory();
        var protector = CreateProtector(keyRing.Path, "audit-test");
        var audits = new PostgresSyncAuditRepository(Database);
        var eventId = Guid.NewGuid();
        var audit = new EntitySyncAuditEvent(
            context.TenantId, eventId, context.Now, "PlanCreated", new EntitySyncActor("actor"),
            null, null, null, null, "correlation", new EntitySyncJsonValue("{\"secret\":\"[redacted]\"}"),
            new EntitySyncSha256(new string('d', 64)), new EntitySyncSha256(new string('e', 64)),
            context.Now.AddMinutes(-1));
        var fullCiphertext = protector.Protect(
            EntitySyncDataProtectionPurpose.AuditValue, "{\"secret\":\"full\"}");
        await audits.AppendAsync(context.TenantId, audit,
            new EntitySyncAuditEventFullValues(context.TenantId, eventId, fullCiphertext,
                context.Now.AddMinutes(-1)), default);
        Assert.Single((await audits.ListAsync(context.TenantId, null, null, 10, default)).Events);
        Assert.Null(await audits.GetFullValuesAsync("other-tenant", eventId, default));
        Assert.Equal(fullCiphertext,
            (await audits.GetFullValuesAsync(context.TenantId, eventId, default))!.FullValuesCiphertext);
        Assert.Equal(1, await audits.DeleteExpiredFullValuesAsync(
            context.TenantId, DateTimeOffset.UtcNow, 1, default));

        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        Assert.False(await plans.TryExpireAsync(
            "other-tenant", manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            EntitySyncDurablePlanStatus.Draft, context.Now.AddDays(2), default));
        Assert.True(await plans.TryExpireAsync(
            context.TenantId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            EntitySyncDurablePlanStatus.Draft, context.Now.AddDays(2), default));
    }

    [Fact]
    public async Task Idempotent_executor_replays_conflicts_and_executes_concurrent_command_once()
    {
        var executor = new PostgresIdempotencyRepository(Database, TimeProvider.System);
        var executions = 0;
        var hash = new string('f', 64);
        Task<IdempotentResponse> Command(CancellationToken _)
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(new IdempotentResponse(201, new EntitySyncJsonValue("{\"id\":1}")));
        }

        var responses = await Task.WhenAll(
            executor.ExecuteAsync("idempotency-tenant", "same-key", hash, Command, default),
            executor.ExecuteAsync("idempotency-tenant", "same-key", hash, Command, default));

        Assert.Equal(1, executions);
        Assert.All(responses, response => Assert.Equal(201, response.StatusCode));
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => executor.ExecuteAsync(
            "idempotency-tenant", "same-key", new string('a', 64), Command, default));
        Assert.Null(await executor.GetAsync("other-tenant", "same-key", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "idempotency-tenant", "failed-key", hash,
            _ => throw new InvalidOperationException("command failed"), default));
        Assert.Null(await executor.GetAsync("idempotency-tenant", "failed-key", default));
    }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = $"Host=127.0.0.1;Database=postgres;Username={Environment.UserName};Pooling=false";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        admin = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        await using (var command = admin.CreateCommand($"CREATE DATABASE \"{databaseName}\""))
            await command.ExecuteNonQueryAsync();
        var databaseBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false
        };
        database = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
        await EntitySyncDatabaseMigrator.ApplyAsync(Database);
    }

    public async Task DisposeAsync()
    {
        if (database is not null) await database.DisposeAsync();
        if (admin is not null)
        {
            await using var command = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
            await command.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private async Task<ControlContext> SeedControlContextAsync(string suffix)
    {
        var tenantId = $"tenant-{suffix}-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var policies = new PostgresSyncPolicyRepository(Database);
        var source = Connection(tenantId, $"source-{suffix}", "NetSuite", 1, "cipher-source", now);
        var target = Connection(tenantId, $"target-{suffix}", "HaloPSA", 1, "cipher-target", now);
        await connections.InsertAsync(tenantId, source, default);
        await connections.InsertAsync(tenantId, target, default);
        var policy = Policy(tenantId, source.ConnectionId, target.ConnectionId, now);
        await policies.InsertAsync(tenantId, policy, default);
        return new ControlContext(tenantId, now, source, target, policy);
    }

    private static EntitySyncConnectionDefinition Connection(
        string tenantId, string connectionId, string vendor, long generation,
        string ciphertext, DateTimeOffset now) =>
        new(
            tenantId, connectionId, vendor, connectionId, generation, true,
            new EntitySyncJsonValue("{\"region\":\"us\"}"), ciphertext,
            now, new EntitySyncActor("creator"), now, new EntitySyncActor("creator"));

    private static EntitySyncPolicy Policy(
        string tenantId, string sourceConnectionId, string targetConnectionId,
        DateTimeOffset now) =>
        EntitySyncPolicy.Create(
            tenantId, Guid.NewGuid(), "policy", "route-a",
            new EntitySyncPolicyDefinition(
                "NetSuite", sourceConnectionId, "Customer", "HaloPSA", targetConnectionId,
                "Client", true, false, 90, 70, "externalId", "customField",
                EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, ["name"], ["password"], true),
            true, now, new EntitySyncActor("creator"));

    private static EntitySyncDurablePlanManifest Manifest(
        ControlContext context, int itemCount, string reason = "exact-id")
    {
        var planId = Guid.NewGuid();
        var unsealedPlan = new EntitySyncDurablePlan(
            context.TenantId, planId, context.Policy.PolicyId, context.Policy.Version,
            context.Policy.DefinitionSha256, "route-a", context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncSha256(new string('0', 64)),
            EntitySyncDurablePlanStatus.Draft,
            new EntitySyncSelectionBounds("active", 10, "source-1"), 0,
            context.Now, new EntitySyncActor("planner"), context.Now.AddDays(1));
        var items = Enumerable.Range(0, itemCount).Select(index =>
            new EntitySyncDurablePlanItem(
                context.TenantId, planId, Guid.NewGuid(), index, "NetSuite",
                context.Source.ConnectionId, "Customer", $"key-{index}", $"SOURCE-{index}",
                "HaloPSA", context.Target.ConnectionId, "Client", $"TARGET-{index}", "Update",
                new EntitySyncMatchEvidence(95, "Exact", [reason]),
                new EntitySyncJsonValue($"{{\"name\":\"before-{index}\"}}"),
                new EntitySyncJsonValue($"{{\"name\":\"desired-{index}\"}}"),
                new EntitySyncSha256(new string('a', 64)),
                new EntitySyncSha256(new string('b', 64)),
                [new EntitySyncFieldDiff("name", new EntitySyncJsonValue("\"before\""),
                    new EntitySyncJsonValue("\"desired\""))])).ToArray();
        return EntitySyncDurablePlanManifest.Create(unsealedPlan, items);
    }

    private static IReadOnlyList<EntitySyncOperationItem> OperationItems(
        EntitySyncOperation operation, IReadOnlyList<EntitySyncDurablePlanItem> planItems,
        DateTimeOffset expiresAt) =>
        planItems.Select(item => EntitySyncOperationItem.Rehydrate(
            operation.TenantId, operation.OperationId, operation.PlanId, item.ItemId,
            item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, item.RedactedBefore, item.RedactedDesired,
            item.BeforePayloadSha256, item.DesiredPayloadSha256, null, expiresAt,
            null, EntitySyncItemOutcome.Pending, null, null, null, null)).ToArray();

    private static IEntitySyncDataProtector CreateProtector(string keyPath, string applicationName)
    {
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keyPath), builder => builder.SetApplicationName(applicationName));
        return new EntitySyncDataProtector(provider);
    }

    private NpgsqlDataSource Database =>
        database ?? throw new InvalidOperationException("The test database is not initialized.");

    private sealed record ControlContext(
        string TenantId,
        DateTimeOffset Now,
        EntitySyncConnectionDefinition Source,
        EntitySyncConnectionDefinition Target,
        EntitySyncPolicy Policy);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"entitysync-dp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
