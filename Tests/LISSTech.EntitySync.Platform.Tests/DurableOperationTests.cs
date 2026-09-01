using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class DurableOperationTests : IAsyncLifetime
{
    private readonly string databaseName = $"entitysync_durable_operations_{Guid.NewGuid():N}";
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource? database;
    private TestFixture? fixture;

    [Fact]
    public void Terminal_statuses_treat_unknown_as_failure_and_skips_as_success()
    {
        Assert.Equal(EntitySyncOperationStatus.Failed,
            SyncOperationService.DeriveTerminalStatus([EntitySyncItemOutcome.Unknown]));
        Assert.Equal(EntitySyncOperationStatus.Partial,
            SyncOperationService.DeriveTerminalStatus(
                [EntitySyncItemOutcome.Succeeded, EntitySyncItemOutcome.Unknown]));
        Assert.Equal(EntitySyncOperationStatus.Failed,
            SyncOperationService.DeriveTerminalStatus(
                [EntitySyncItemOutcome.Skipped, EntitySyncItemOutcome.Unknown]));
        Assert.Equal(EntitySyncOperationStatus.Failed,
            SyncOperationService.DeriveTerminalStatus(
                [EntitySyncItemOutcome.Skipped, EntitySyncItemOutcome.Failed]));
        Assert.Equal(EntitySyncOperationStatus.Succeeded,
            SyncOperationService.DeriveTerminalStatus(
                [EntitySyncItemOutcome.Succeeded, EntitySyncItemOutcome.Skipped]));
        Assert.Equal(EntitySyncOperationStatus.Cancelled,
            SyncOperationService.DeriveTerminalStatus(
                [EntitySyncItemOutcome.Pending], cancellationRequestedBeforeDispatch: true));
    }
    [Fact]
    public async Task Attributed_tools_use_durable_plan_approval_and_operation_identities()
    {
        var context = new McpRequestContext(Fixture.Tenant, false);
        var createdJson = await SyncTools.CreateSyncPlan(
            Fixture.DurablePlanning, context, Fixture.PolicyId.ToString(),
            "mcp-durable-plan", sourceEntityId: Fixture.SourceEntity.Id);
        using var created = JsonDocument.Parse(createdJson);
        Assert.True(created.RootElement.GetProperty("success").GetBoolean());
        var planId = created.RootElement.GetProperty("planId").GetString()!;
        var digest = created.RootElement.GetProperty("digest").GetString()!;

        var inspectedJson = await SyncTools.GetSyncPlan(
            Fixture.DurablePlanning, context, planId, 1, 25);
        using var inspected = JsonDocument.Parse(inspectedJson);
        Assert.True(inspected.RootElement.GetProperty("success").GetBoolean());
        Assert.True(inspected.RootElement.GetProperty("result")
            .GetProperty("inspectionComplete").GetBoolean());

        var approvedJson = await SyncTools.ApproveSyncPlan(
            Fixture.DurablePlanning, context, planId, digest);
        using var approved = JsonDocument.Parse(approvedJson);
        Assert.True(approved.RootElement.GetProperty("success").GetBoolean());
        var approvalId = approved.RootElement.GetProperty("approvalId").GetString()!;

        var dryRunJson = await SyncTools.ApplySyncPlan(
            Fixture.Coordinator, context, planId, "mcp-dry-run");
        using var dryRun = JsonDocument.Parse(dryRunJson);
        Assert.True(dryRun.RootElement.GetProperty("success").GetBoolean());
        Assert.NotEqual(Guid.Empty,
            dryRun.RootElement.GetProperty("operationId").GetGuid());

        var applyJson = await SyncTools.ApplySyncPlan(
            Fixture.Coordinator, context, planId, "mcp-apply", true, approvalId);
        using var apply = JsonDocument.Parse(applyJson);
        Assert.True(apply.RootElement.GetProperty("success").GetBoolean());
        var operationId = apply.RootElement.GetProperty("operationId").GetString()!;
        var statusJson = await SyncTools.GetSyncPlanApply(
            Fixture.Coordinator, context, operationId, default);
        using var status = JsonDocument.Parse(statusJson);
        Assert.True(status.RootElement.GetProperty("success").GetBoolean());
    }


    [Fact]
    public void Vendor_request_id_is_deterministic_redacted_and_item_specific()
    {
        var operationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var firstItem = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var secondItem = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var first = EntitySyncOperationWorker.CreateVendorRequestId(operationId, firstItem);
        Assert.Equal(first, EntitySyncOperationWorker.CreateVendorRequestId(operationId, firstItem));
        Assert.NotEqual(first, EntitySyncOperationWorker.CreateVendorRequestId(operationId, secondItem));
        Assert.DoesNotContain(operationId.ToString(), first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dry_run_is_idempotent_never_consumes_approval_and_retains_encrypted_evidence()
    {
        var plan = await Fixture.CreatePlanAsync(approved: false, action: "Update");
        var first = await Fixture.Service.QueueDryRunAsync(
            Fixture.Tenant, plan.Plan.PlanId, "dry-key", Fixture.Actor, default);
        var replay = await Fixture.Service.QueueDryRunAsync(
            Fixture.Tenant, plan.Plan.PlanId, "dry-key", Fixture.Actor, default);
        Assert.Equal(first.OperationId, replay.OperationId);
        await Assert.ThrowsAsync<SyncOperationIdempotencyConflictException>(() =>
            Fixture.Service.QueueDryRunAsync(
                Fixture.Tenant, plan.Plan.PlanId, "dry-key",
                new EntitySyncActor("other-actor"), default));

        var completed = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "dry-worker", default);
        Assert.NotNull(completed);
        Assert.Equal(EntitySyncOperationStatus.Succeeded, completed!.Status);
        Assert.Equal(1, completed.SucceededCount);
        Assert.Equal(0, Fixture.Target.WriteCalls);
        var item = Assert.Single(await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, first.OperationId, default));
        Assert.Equal(EntitySyncItemOutcome.Succeeded, item.Outcome);
        Assert.Null(item.DispatchStartedAt);
        var snapshot = await Fixture.Operations.GetSnapshotAsync(
            Fixture.Tenant, first.OperationId, item.ItemId, default);
        Assert.NotNull(snapshot?.EncryptedBeforeCiphertext);
        Assert.DoesNotContain("Before", snapshot!.EncryptedBeforeCiphertext!, StringComparison.Ordinal);
        Assert.InRange(snapshot.ExpiresAt,
            first.CreatedAt.AddDays(364), first.CreatedAt.AddDays(366));
        Assert.Equal(EntitySyncDurablePlanStatus.Draft,
            (await Fixture.Plans.GetAsync(Fixture.Tenant, plan.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Concurrent_apply_consumes_exact_approval_once_and_duplicate_request_replays()
    {
        var plan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        var calls = Enumerable.Range(0, 8).Select(_ => Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, plan.Plan.PlanId, plan.Approval!.ApprovalId,
            "apply-key", Fixture.Actor, default));
        var results = await Task.WhenAll(calls);
        Assert.Single(results.Select(result => result.OperationId).Distinct());
        var stored = await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, results[0].OperationId, default);
        Assert.Single(stored);

        var other = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        var races = new[]
        {
            Fixture.Service.QueueApplyAsync(
                Fixture.Tenant, other.Plan.PlanId, other.Approval!.ApprovalId,
                "race-a", Fixture.Actor, default),
            Fixture.Service.QueueApplyAsync(
                Fixture.Tenant, other.Plan.PlanId, other.Approval.ApprovalId,
                "race-b", Fixture.Actor, default)
        };
        var outcomes = await Task.WhenAll(races.Select(async task =>
        {
            try { return (Success: true, Operation: await task); }
            catch (DurablePlanApprovalConflictException) { return (false, null); }
        }));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Success));
    }

    [Fact]
    public async Task Apply_persists_before_and_after_snapshots_checkpoint_audit_and_terminal_counts()
    {
        var plan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        var queued = await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, plan.Plan.PlanId, plan.Approval!.ApprovalId,
            "normal-apply", Fixture.Actor, default);
        var completed = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "worker-normal", default);
        var diagnosticItem = Assert.Single(await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, queued.OperationId, default));
        Assert.True(
            completed!.Status == EntitySyncOperationStatus.Succeeded,
            $"status={completed.Status}; outcome={diagnosticItem.Outcome}; code={diagnosticItem.SafeWriteCode}");
        Assert.Equal(1, completed.TotalCount);
        Assert.Equal(1, completed.SucceededCount);
        Assert.Equal(0, completed.UnknownCount);
        Assert.Equal(1, Fixture.Target.WriteCalls);
        Assert.NotNull(diagnosticItem.DispatchStartedAt);
        Assert.Equal(EntitySyncItemOutcome.Succeeded, diagnosticItem.Outcome);
        Assert.Equal(diagnosticItem.DesiredPayloadSha256, diagnosticItem.AfterPayloadSha256);
        var snapshot = await Fixture.Operations.GetSnapshotAsync(
            Fixture.Tenant, queued.OperationId, diagnosticItem.ItemId, default);
        Assert.NotNull(snapshot?.EncryptedBeforeCiphertext);
        Assert.NotNull(snapshot?.EncryptedAfterCiphertext);
        var audit = await Fixture.AuditRepository.ListAsync(
            Fixture.Tenant, null, null, 20, default);
        Assert.Contains(audit.Events, value =>
            value.EventType == "SyncOperationItemSucceeded"
            && value.ItemId == diagnosticItem.ItemId);
    }

    [Fact]
    public async Task Changed_exclusion_and_generation_rotation_block_dispatch_without_vendor_write()
    {
        var createPlan = await Fixture.CreatePlanAsync(approved: true, action: "Create");
        var createRun = await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, createPlan.Plan.PlanId, createPlan.Approval!.ApprovalId,
            "excluded-create", Fixture.Actor, default);
        await Fixture.Exclusions.AddAsync(
            EntityExclusionRoute.Create(
                Fixture.Tenant, "NetSuite", Fixture.SourceConnectionId, "Customer",
                "HaloPSA", Fixture.TargetConnectionId, "Client"),
            Fixture.SourceEntity.Id, Fixture.SourceEntity.Name, "blocked", "tester", default);
        var excluded = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "worker-exclusion", default);
        Assert.Equal(EntitySyncOperationStatus.Succeeded, excluded!.Status);
        Assert.Equal(1, excluded.SkippedCount);
        Assert.Equal(0, Fixture.Target.WriteCalls);
        Assert.Equal(EntitySyncItemOutcome.Skipped,
            (await Fixture.Operations.GetItemsAsync(
                Fixture.Tenant, createRun.OperationId, default)).Single().Outcome);

        await Fixture.Exclusions.RevokeAsync(
            EntityExclusionRoute.Create(
                Fixture.Tenant, "NetSuite", Fixture.SourceConnectionId, "Customer",
                "HaloPSA", Fixture.TargetConnectionId, "Client"),
            Fixture.SourceEntity.Id, "tester", default);
        await Fixture.ResetTargetAsync();
        var rotatedPlan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, rotatedPlan.Plan.PlanId, rotatedPlan.Approval!.ApprovalId,
            "rotated", Fixture.Actor, default);
        var current = (await Fixture.ConnectionDefinitions.GetAsync(
            Fixture.Tenant, Fixture.TargetConnectionId, default))!;
        await Fixture.ConnectionDefinitions.TryReplaceAsync(
            Fixture.Tenant, Fixture.TargetConnectionId, current.Generation,
            current.NextGeneration(
                current.DisplayName, true, current.PublicConfiguration,
                current.SecretCiphertext, Fixture.Actor, DateTimeOffset.UtcNow), default);
        var rotated = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "worker-rotation", default);
        Assert.Equal(EntitySyncOperationStatus.Failed, rotated!.Status);
        Assert.Equal(0, Fixture.Target.WriteCalls);
    }

    [Fact]
    public async Task Lost_response_crash_restart_and_checkpoint_failure_never_redispatch()
    {
        var plan = await Fixture.CreatePlanAsync(approved: true, action: "Update",
            updatePolicy: EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly);
        var run = await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, plan.Plan.PlanId, plan.Approval!.ApprovalId,
            "lost-response", Fixture.Actor, default);
        Fixture.Target.ThrowAfterWrite = true;
        Fixture.Target.HideReads = true;
        var first = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "worker-crash-a", default);
        var firstItem = (await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, run.OperationId, default)).Single();
        Assert.True(
            first.UnknownCount == 1,
            $"status={first.Status}; outcome={firstItem.Outcome}; code={firstItem.SafeWriteCode}");
        Assert.Equal(1, Fixture.Target.WriteCalls);
        Assert.Equal(EntitySyncItemOutcome.Unknown, firstItem.Outcome);

        Fixture.Target.ThrowAfterWrite = false;
        Fixture.Target.HideReads = false;
        await Fixture.DelayCheckpointWritesAsync(true);
        var checkpointUnknown = await Fixture.Reconciler.ReconcileAsync(
            Fixture.Tenant, run.OperationId, plan.Item.ItemId,
            "reconcile-checkpoint-failure", default);
        Assert.Null(checkpointUnknown);
        var stillUnknown = await Fixture.Operations.GetItemAsync(
            Fixture.Tenant, run.OperationId, plan.Item.ItemId, default);
        Assert.Equal(EntitySyncItemOutcome.Unknown, stillUnknown!.Outcome);
        var route = EntitySyncChangeStateRoute.Create(
            Fixture.Tenant, plan.Plan.RouteScope, plan.Item.SourceVendor,
            plan.Item.SourceConnectionId, plan.Item.SourceEntityType,
            plan.Item.TargetVendor, plan.Item.TargetConnectionId,
            plan.Item.TargetEntityType);
        var checkpoints = await new PostgresEntitySyncChangeStateRepository(Database)
            .GetBySourceIdsAsync(route, [plan.Item.SourceEntityId], default);
        Assert.Empty(checkpoints);
        var auditPage = await Fixture.AuditRepository.ListAsync(
            Fixture.Tenant, null, null, 100, default);
        Assert.DoesNotContain(auditPage.Events, audit =>
            audit.EventType == "SyncOperationItemSucceeded"
            && audit.ItemId == plan.Item.ItemId);
        var checkpointSnapshot = await Fixture.Operations.GetSnapshotAsync(
            Fixture.Tenant, run.OperationId, plan.Item.ItemId, default);
        await Fixture.DelayCheckpointWritesAsync(false);
        Assert.NotNull(checkpointSnapshot?.EncryptedAfterCiphertext);
        Assert.Equal(1, Fixture.Target.WriteCalls);
        var reconciled = await Fixture.Reconciler.ReconcileAsync(
            Fixture.Tenant, run.OperationId, plan.Item.ItemId,
            "reconcile-restart", default);
        Assert.True(
            reconciled!.Outcome == EntitySyncItemOutcome.Succeeded,
            $"outcome={reconciled.Outcome}; code={reconciled.SafeWriteCode}");
        Assert.Equal(1, Fixture.Target.WriteCalls);
        var final = await Fixture.Operations.GetAsync(
            Fixture.Tenant, run.OperationId, default);
        Assert.Equal(EntitySyncOperationStatus.Succeeded, final!.Status);
        Assert.Equal(1, final.SucceededCount);
    }

    [Fact]
    public async Task Immutable_target_id_prevents_fallback_to_a_different_desired_match()
    {
        var plan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        var run = await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, plan.Plan.PlanId, plan.Approval!.ApprovalId,
            "immutable-target", Fixture.Actor, default);
        Fixture.Target.ThrowAfterWrite = true;
        Fixture.Target.HideReads = true;
        var unknown = await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "worker-immutable-target", default);
        Assert.Equal(EntitySyncOperationStatus.Failed, unknown!.Status);
        var item = Assert.Single(await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, run.OperationId, default));
        Assert.Equal(EntitySyncItemOutcome.Unknown, item.Outcome);

        Fixture.Target.ThrowAfterWrite = false;
        Fixture.Target.HideReads = false;
        Fixture.TargetEntity.Id = "target-entity-b";
        var reconciled = await Fixture.Reconciler.ReconcileAsync(
            Fixture.Tenant, run.OperationId, item.ItemId,
            "reconcile-immutable-target", default);

        Assert.Equal(EntitySyncItemOutcome.Unknown, reconciled!.Outcome);
        Assert.NotEqual("target-entity-b", reconciled.VendorTargetEntityId);
        Assert.Equal(1, Fixture.Target.WriteCalls);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_cancels_but_after_dispatch_is_unknown_and_not_retried()
    {
        var beforePlan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, beforePlan.Plan.PlanId, beforePlan.Approval!.ApprovalId,
            "cancel-before", Fixture.Actor, default);
        Fixture.Source.BlockReads = true;
        using var beforeCancellation = new CancellationTokenSource();
        var beforeTask = Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "cancel-before-worker", beforeCancellation.Token);
        await Fixture.Source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        beforeCancellation.Cancel();
        var cancelled = await beforeTask;
        Assert.Equal(EntitySyncOperationStatus.Cancelled, cancelled!.Status);
        Assert.Equal(0, Fixture.Target.WriteCalls);

        Fixture.Source.ReleaseReads();
        await Fixture.ResetTargetAsync();
        var afterPlan = await Fixture.CreatePlanAsync(approved: true, action: "Update");
        await Fixture.Service.QueueApplyAsync(
            Fixture.Tenant, afterPlan.Plan.PlanId, afterPlan.Approval!.ApprovalId,
            "cancel-after", Fixture.Actor, default);
        Fixture.Target.BlockWrites = true;
        Fixture.Target.HideReads = true;
        using var afterCancellation = new CancellationTokenSource();
        var afterTask = Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "cancel-after-worker", afterCancellation.Token);
        await Fixture.Target.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        afterCancellation.Cancel();
        var uncertain = await afterTask;
        Assert.Equal(1, Fixture.Target.WriteCalls);
        Assert.Equal(1, uncertain!.UnknownCount);
        await Fixture.Worker.ExecuteOneAsync(
            Fixture.Tenant, "cancel-restart-worker", default);
        Assert.Equal(1, Fixture.Target.WriteCalls);
    }

    [Fact]
    public async Task Repository_fences_stale_lease_owner_and_reconciliation_attempt()
    {
        var plan = await Fixture.CreatePlanAsync(approved: false, action: "Update");
        var queued = await Fixture.Service.QueueDryRunAsync(
            Fixture.Tenant, plan.Plan.PlanId, "fence", Fixture.Actor, default);
        var leased = await Fixture.Operations.TryLeaseNextAsync(
            Fixture.Tenant, "owner-a", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2), default);
        var running = leased!.Start(DateTimeOffset.UtcNow);
        Assert.True(await Fixture.Operations.TryReplaceAsync(
            Fixture.Tenant, queued.OperationId, EntitySyncOperationStatus.Leased,
            running, default));
        var item = Assert.Single(await Fixture.Operations.GetItemsAsync(
            Fixture.Tenant, queued.OperationId, default));
        var failed = new EntitySyncOperationItem(
            item.TenantId, item.OperationId, item.PlanId, item.ItemId,
            item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, item.RedactedBefore, item.RedactedDesired,
            item.BeforePayloadSha256, item.DesiredPayloadSha256, null,
            item.SnapshotsExpireAt, item.VendorRequestId,
            EntitySyncItemOutcome.Failed, "STALE_OWNER",
            "A stale owner must not write.", item.StartedAt, DateTimeOffset.UtcNow);
        Assert.False(await Fixture.Operations.TryRecordItemAsync(
            Fixture.Tenant, queued.OperationId, queued.PlanId, item.ItemId,
            running.Attempt, "owner-b", EntitySyncItemOutcome.Pending,
            failed, null, default));
        Assert.Equal(EntitySyncItemOutcome.Pending,
            (await Fixture.Operations.GetItemAsync(
                Fixture.Tenant, queued.OperationId, item.ItemId, default))!.Outcome);
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
        fixture = await TestFixture.CreateAsync(Database);
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

    private TestFixture Fixture => fixture ?? throw new InvalidOperationException();
    private NpgsqlDataSource Database => database ?? throw new InvalidOperationException();

    private sealed class TestFixture
    {
        internal string Tenant => "tenant-durable";
        internal string SourceConnectionId => "source-1";
        internal string TargetConnectionId => "target-1";
        internal readonly EntitySyncActor Actor = new("tester");
        private readonly NpgsqlDataSource database;
        private readonly FakeRuntime runtime;
        private readonly TestProtector protector;
        private readonly TestMapper mapper;
        private readonly EntitySyncPolicy policy;
        private long targetGeneration = 1;

        private TestFixture(NpgsqlDataSource database)
        {
            this.database = database;
            Operations = new PostgresSyncOperationRepository(database);
            Plans = new PostgresDurableSyncPlanRepository(database);
            Policies = new PostgresSyncPolicyRepository(database);
            ConnectionDefinitions = new PostgresConnectionDefinitionRepository(database);
            Exclusions = new PostgresEntityExclusionRepository(database);
            AuditRepository = new PostgresSyncAuditRepository(database);
            protector = new TestProtector();
            mapper = new TestMapper();
            SourceEntity = new ExternalEntity
            {
                Vendor = "NetSuite", EntityType = "Customer", Id = "source-entity-1",
                Name = "Desired"
            };
            TargetEntity = new ExternalEntity
            {
                Vendor = "HaloPSA", EntityType = "Client", Id = "target-entity-1",
                Name = "Before"
            };
            Source = new FakeAdapter("NetSuite", () => [SourceEntity]);
            Target = new FakeAdapter("HaloPSA", () => [TargetEntity]);
            runtime = new FakeRuntime(Source, Target);
            ChangeStates = new TestChangeStates();
            var legacyPlans = new InMemoryEntitySyncPlanRepository();
            var planner = new EntitySyncPlanner(
                runtime, legacyPlans, Exclusions, new WeightedEntityMatcher(),
                mapper, ChangeStates);
            var definition = new EntitySyncPolicyDefinition(
                "NetSuite", SourceConnectionId, "Customer",
                "HaloPSA", TargetConnectionId, "Client",
                true, true, 90, 70, "Id", "ExternalId",
                EntitySyncUpdatePolicy.Standard,
                ["name"], [], false);
            policy = EntitySyncPolicy.Create(
                Tenant, Guid.NewGuid(), "policy", new string('a', 64), definition,
                true, DateTimeOffset.UtcNow, Actor);
            Service = new SyncOperationService(
                Plans, Operations, Policies, ConnectionDefinitions);
            DurablePlanning = new DurablePlanService(
                planner, new PlanManifestBuilder(mapper), Policies,
                ConnectionDefinitions, runtime, Exclusions, Plans,
                TimeProvider.System);
            var legacyService = new EntitySyncService(
                planner, runtime, legacyPlans, Exclusions, mapper, ChangeStates,
                operationService: Service);
            Coordinator = new EntitySyncApplyCoordinator(
                legacyService, legacyPlans, new TestLifetime(),
                operationRepository: Operations);
            var auditService = new SyncAuditService(AuditRepository, protector);
            Reconciler = new VendorOutcomeReconciler(
                Operations, Plans, Policies, runtime, protector, auditService,
                reconciliationLease: TimeSpan.FromMilliseconds(250));
            Worker = new EntitySyncOperationWorker(
                Operations, Plans, Policies, runtime, mapper, protector,
                Reconciler, auditService,
                options: new EntitySyncOperationWorkerOptions(TimeSpan.FromSeconds(2)));
            
        }

        internal async Task DelayCheckpointWritesAsync(bool delay)
        {
            await using var command = database.CreateCommand(delay
                ? """
                  CREATE OR REPLACE FUNCTION entitysync.test_delay_checkpoint()
                  RETURNS trigger LANGUAGE plpgsql AS $$
                  BEGIN
                      PERFORM pg_sleep(0.5);
                      RETURN NEW;
                  END;
                  $$;
                  DROP TRIGGER IF EXISTS test_delay_checkpoint
                      ON entitysync.entity_change_state;
                  CREATE TRIGGER test_delay_checkpoint
                      BEFORE INSERT OR UPDATE ON entitysync.entity_change_state
                      FOR EACH ROW EXECUTE FUNCTION entitysync.test_delay_checkpoint();
                  """
                : """
                  DROP TRIGGER IF EXISTS test_delay_checkpoint
                      ON entitysync.entity_change_state;
                  """);
            await command.ExecuteNonQueryAsync();
        }

        internal PostgresSyncOperationRepository Operations { get; }
        internal PostgresDurableSyncPlanRepository Plans { get; }
        internal PostgresSyncPolicyRepository Policies { get; }
        internal PostgresConnectionDefinitionRepository ConnectionDefinitions { get; }
        internal PostgresEntityExclusionRepository Exclusions { get; }
        internal PostgresSyncAuditRepository AuditRepository { get; }
        internal TestChangeStates ChangeStates { get; }
        internal Guid PolicyId => policy.PolicyId;
        internal DurablePlanService DurablePlanning { get; }
        internal EntitySyncApplyCoordinator Coordinator { get; }
        internal SyncOperationService Service { get; }
        internal EntitySyncOperationWorker Worker { get; }
        internal VendorOutcomeReconciler Reconciler { get; }
        internal FakeAdapter Source { get; }
        internal FakeAdapter Target { get; }
        internal ExternalEntity SourceEntity { get; }
        internal ExternalEntity TargetEntity { get; private set; }

        internal static async Task<TestFixture> CreateAsync(NpgsqlDataSource database)
        {
            var fixture = new TestFixture(database);
            await fixture.SeedControlAsync();
            return fixture;
        }

        internal async Task<PlanResult> CreatePlanAsync(
            bool approved,
            string action,
            EntitySyncUpdatePolicy updatePolicy = EntitySyncUpdatePolicy.Standard)
        {
            var effectivePolicy = updatePolicy == policy.Definition.UpdatePolicy
                ? policy
                : policy.NextVersion(
                    Actor,
                    new EntitySyncPolicyDefinition(
                        "NetSuite", SourceConnectionId, "Customer",
                        "HaloPSA", TargetConnectionId, "Client", true, true,
                        90, 70, "Id", "ExternalId", updatePolicy,
                        ["name"], [], false),
                    DateTimeOffset.UtcNow);
            if (effectivePolicy.Version != policy.Version)
                await Policies.InsertAsync(Tenant, effectivePolicy, default);
            var now = DateTimeOffset.UtcNow;

            var desiredPayload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = JsonSerializer.SerializeToElement(SourceEntity.Name)
            };
            var beforePayload = action == "Create"
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = JsonSerializer.SerializeToElement(TargetEntity.Name)
                };
            var planId = Guid.NewGuid();
            var plan = new EntitySyncDurablePlan(
                Tenant, planId, effectivePolicy.PolicyId, effectivePolicy.Version,
                effectivePolicy.DefinitionSha256, effectivePolicy.RouteScope,
                SourceConnectionId, 1, TargetConnectionId, targetGeneration,
                new EntitySyncSha256(new string('0', 64)),
                EntitySyncDurablePlanStatus.Draft,
                new EntitySyncSelectionBounds(null, null, SourceEntity.Id),
                0, now, Actor, now.AddHours(4));
            var item = new EntitySyncDurablePlanItem(
                Tenant, planId, Guid.NewGuid(), 0,
                "NetSuite", SourceConnectionId, "Customer",
                SourceEntity.Id.ToLowerInvariant(), SourceEntity.Id,
                "HaloPSA", TargetConnectionId, "Client",
                action == "Create" ? null : TargetEntity.Id,
                action, new EntitySyncMatchEvidence(100, "Exact", []),
                JsonValue(beforePayload),
                JsonValue(desiredPayload),
                action == "Create" ? null : HashPayload(beforePayload),
                HashPayload(desiredPayload),
                [new EntityFieldChange(
                    "name",
                    new EntitySyncJsonValue(JsonSerializer.Serialize(TargetEntity.Name)),
                    new EntitySyncJsonValue(JsonSerializer.Serialize(SourceEntity.Name)),
                    EntitySyncCanonicalDigest.Compute(TargetEntity.Name),
                    EntitySyncCanonicalDigest.Compute(SourceEntity.Name),
                    false)]);
            var manifest = EntitySyncDurablePlanManifest.Create(plan, [item]);
            await Plans.InsertAsync(Tenant, manifest, default);
            if (!approved) return new PlanResult(manifest.Plan, item, null);
            var inspectionId = Guid.NewGuid();
            await Plans.GetOrOpenInspectionAsync(
                Tenant, inspectionId, planId, manifest.Plan.PlanDigestSha256,
                SourceConnectionId, 1, TargetConnectionId, targetGeneration,
                Actor, now, default);
            await Plans.RecordInspectionRangeAsync(
                Tenant, inspectionId, Guid.NewGuid(), 0, 0, now, default);
            await Plans.CompleteInspectionAsync(
                Tenant, inspectionId, planId, manifest.Plan.PlanDigestSha256,
                SourceConnectionId, 1, TargetConnectionId, targetGeneration,
                now, default);
            var approvalId = Guid.NewGuid();
            var auditElement = JsonSerializer.SerializeToElement(new
            {
                PlanId = planId,
                Digest = manifest.Plan.PlanDigestSha256.Value,
                InspectionId = inspectionId,
                manifest.Plan.PolicyId,
                manifest.Plan.PolicyVersion,
                manifest.Plan.SourceConnectionId,
                manifest.Plan.SourceConnectionGeneration,
                manifest.Plan.TargetConnectionId,
                manifest.Plan.TargetConnectionGeneration
            });
            var auditValues = new EntitySyncJsonValue(auditElement.GetRawText());
            var approval = await Plans.ApproveInspectionAsync(
                Tenant, approvalId, inspectionId, planId,
                manifest.Plan.PlanDigestSha256, SourceConnectionId, 1,
                TargetConnectionId, targetGeneration, Actor, now,
                now.AddHours(4), new EntitySyncAuditEvent(
                    Tenant, Guid.NewGuid(), now, "SyncPlanApproved", Actor,
                    null, null, planId, null, approvalId.ToString("N"),
                    auditValues, EntitySyncCanonicalDigest.Compute(auditElement),
                    null, null), default);
            return new PlanResult(
                (await Plans.GetAsync(Tenant, planId, default))!, item, approval);
        }

        internal async Task ResetTargetAsync()
        {
            TargetEntity = new ExternalEntity
            {
                Vendor = "HaloPSA", EntityType = "Client", Id = "target-entity-1",
                Name = "Before"
            };
            Target.Reset();
            await Task.CompletedTask;
        }

        private async Task SeedControlAsync()
        {
            var now = DateTimeOffset.UtcNow;
            await ConnectionDefinitions.InsertAsync(Tenant,
                new EntitySyncConnectionDefinition(
                    Tenant, SourceConnectionId, "NetSuite", "Source", 1, true,
                    new EntitySyncJsonValue("{}"), "cipher", now, Actor, now, Actor), default);
            await ConnectionDefinitions.InsertAsync(Tenant,
                new EntitySyncConnectionDefinition(
                    Tenant, TargetConnectionId, "HaloPSA", "Target", 1, true,
                    new EntitySyncJsonValue("{}"), "cipher", now, Actor, now, Actor), default);
            await Policies.InsertAsync(Tenant, policy, default);
        }

        internal sealed record PlanResult(
            EntitySyncDurablePlan Plan,
            EntitySyncDurablePlanItem Item,
            EntitySyncApproval? Approval);
        private static EntitySyncJsonValue JsonValue(
            IReadOnlyDictionary<string, JsonElement> payload) =>
            new(JsonSerializer.Serialize(payload));

        private static EntitySyncSha256 HashPayload(
            IReadOnlyDictionary<string, JsonElement> payload) =>
            EntitySyncCanonicalDigest.Compute(
                JsonSerializer.SerializeToElement(payload));

    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }

    private sealed class TestMapper : IEntityMapper
    {
        public EntityWriteRequest MapCreate(
            ExternalEntity source, string targetVendor, string targetEntityType,
            MatchOptions options) => new()
        {
            Vendor = targetVendor, EntityType = targetEntityType, Name = source.Name
        };

        public EntityWriteRequest MapUpdate(
            ExternalEntity source, ExternalEntity target, MatchOptions options) => new()
        {
            Vendor = target.Vendor, EntityType = target.EntityType,
            Id = target.Id, Name = source.Name
        };
    }

    private sealed class FakeAdapter(
        string vendor,
        Func<IReadOnlyList<ExternalEntity>> readEntities) : IEntityAdapter
    {
        private TaskCompletionSource releaseReads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource releaseWrites =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteCalls { get; private set; }
        internal bool ThrowAfterWrite { get; set; }
        internal bool HideReads { get; set; }
        internal bool BlockReads { get; set; }
        internal bool BlockWrites { get; set; }
        public string Vendor => vendor;
        public IReadOnlyList<string> LookupTypes => [];

        public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            if (BlockReads) await releaseReads.Task.WaitAsync(cancellationToken);
            if (HideReads && WriteCalls > 0) return [];
            var values = readEntities();
            return query.Search is null
                ? values
                : values.Where(value =>
                    value.Id.Equals(query.Search, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public async Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request, CancellationToken cancellationToken) =>
            await WriteAsync(request, "Create", cancellationToken);

        public async Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request, CancellationToken cancellationToken) =>
            await WriteAsync(request, "Update", cancellationToken);

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        internal void ReleaseReads()
        {
            BlockReads = false;
            releaseReads.TrySetResult();
        }

        internal void Reset()
        {
            WriteCalls = 0;
            ThrowAfterWrite = false;
            HideReads = false;
            BlockReads = false;
            BlockWrites = false;
            ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task<EntityWriteResult> WriteAsync(
            EntityWriteRequest request, string action, CancellationToken cancellationToken)
        {
            WriteCalls++;
            WriteStarted.TrySetResult();
            if (BlockWrites) await releaseWrites.Task.WaitAsync(cancellationToken);
            var entity = readEntities().FirstOrDefault();
            if (entity is not null) entity.Name = request.Name;
            if (ThrowAfterWrite) throw new TimeoutException();
            return new EntityWriteResult
            {
                Vendor = Vendor, EntityType = request.EntityType,
                Id = request.Id ?? entity?.Id, Action = action, Success = true,
                VendorRequestId = request.VendorRequestId,
                SafeCode = "OK"
            };
        }
    }

    private sealed class FakeRuntime(FakeAdapter source, FakeAdapter target)
        : IConnectionRuntimeFactory
    {
        public Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId, string connectionId, long expectedGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IConnectionRuntimeLease>(new Lease(
                new EntitySyncConnectionDefinition(
                    tenantId, connectionId,
                    connectionId == "source-1" ? "NetSuite" : "HaloPSA",
                    connectionId, expectedGeneration, true,
                    new EntitySyncJsonValue("{}"), "cipher", DateTimeOffset.UtcNow,
                    new EntitySyncActor("test"), DateTimeOffset.UtcNow,
                    new EntitySyncActor("test")),
                connectionId == "source-1" ? source : target));

        public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId, string vendor, string? connectionId,
            CancellationToken cancellationToken) =>
            AcquireAsync(tenantId, connectionId!, 1, cancellationToken);

        public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId, string vendor, string? connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class Lease(
            EntitySyncConnectionDefinition definition,
            IEntityAdapter adapter) : IConnectionRuntimeLease
        {
            public EntitySyncConnectionDefinition Definition => definition;
            public IEntityAdapter Adapter => adapter;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestProtector : IEntitySyncDataProtector
    {
        public string Protect(EntitySyncDataProtectionPurpose purpose, string plaintext) =>
            $"cipher:{Guid.NewGuid():N}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext))}";

        public string Unprotect(EntitySyncDataProtectionPurpose purpose, string ciphertext) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(
                ciphertext[(ciphertext.LastIndexOf(':') + 1)..]));
    }

    private sealed class TestChangeStates : IEntitySyncChangeStateRepository
    {
        internal bool ThrowOnUpsert { get; set; }
        public Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
            EntitySyncChangeStateRoute route, IReadOnlyCollection<string> sourceEntityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, EntitySyncChangeState>>(
                new Dictionary<string, EntitySyncChangeState>());

        public Task UpsertAsync(
            EntitySyncChangeState state, CancellationToken cancellationToken)
        {
            if (ThrowOnUpsert) throw new InvalidOperationException("checkpoint failure");
            return Task.CompletedTask;
        }
    }
}
