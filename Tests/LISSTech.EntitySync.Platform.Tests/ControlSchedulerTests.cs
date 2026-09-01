using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Scheduler;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlSchedulerTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Schedule_edits_and_disable_create_immutable_versions()
    {
        var policies = new SchedulePolicyRepository(Policy(safe: true));
        var schedules = new MemoryScheduleRepository();
        var service = new SyncScheduleService(schedules, policies, new ManualTimeProvider(Noon));
        var actor = new EntitySyncActor("scheduler-admin");

        var first = await service.CreateAsync(
            "tenant", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new SyncScheduleRequest("nightly", policies.Policy.PolicyId, 1, "*/30 * * * *", "UTC", true),
            actor, default);
        var second = await service.CreateNextVersionAsync(
            "tenant", first.ScheduleId, 1,
            new SyncScheduleRequest("nightly", policies.Policy.PolicyId, 1, "0 * * * *", "UTC", true),
            actor, default);
        var disabled = await service.DisableAsync("tenant", first.ScheduleId, 2, actor, default);

        Assert.Equal([1, 2, 3], schedules.Versions.Select(value => value.Version).ToArray());
        Assert.True(first.Enabled);
        Assert.True(second.Enabled);
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextRunAt);
        Assert.Equal(Noon.AddMinutes(30), first.NextRunAt);
        Assert.Equal(Noon.AddHours(1), second.NextRunAt);
    }

    [Fact]
    public async Task Schedule_requires_valid_timezone_and_safe_subset_policy()
    {
        var actor = new EntitySyncActor("scheduler-admin");
        var unsafePolicies = new SchedulePolicyRepository(Policy(safe: false));
        var unsafeService = new SyncScheduleService(
            new MemoryScheduleRepository(), unsafePolicies, new ManualTimeProvider(Noon));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeService.CreateAsync(
            "tenant", Guid.NewGuid(),
            new SyncScheduleRequest("unsafe", unsafePolicies.Policy.PolicyId, 1, "0 * * * *", "UTC", true),
            actor, default));

        var safePolicies = new SchedulePolicyRepository(Policy(safe: true));
        var safeService = new SyncScheduleService(
            new MemoryScheduleRepository(), safePolicies, new ManualTimeProvider(Noon));
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() => safeService.CreateAsync(
            "tenant", Guid.NewGuid(),
            new SyncScheduleRequest("bad-zone", safePolicies.Policy.PolicyId, 1, "0 * * * *", "Mars/Olympus", true),
            actor, default));
        await Assert.ThrowsAnyAsync<Exception>(() => safeService.CreateAsync(
            "tenant", Guid.NewGuid(),
            new SyncScheduleRequest("six-fields", safePolicies.Policy.PolicyId, 1, "0 0 * * * *", "UTC", true),
            actor, default));
    }

    [Fact]
    public void Cron_next_run_is_DST_deterministic()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var spring = SyncScheduleService.GetNextRun(
            "30 2 * * *", zone,
            new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero));
        var fallFirst = SyncScheduleService.GetNextRun(
            "30 1 * * *", zone,
            new DateTimeOffset(2026, 11, 1, 4, 59, 0, TimeSpan.Zero));
        var fallNext = SyncScheduleService.GetNextRun(
            "30 1 * * *", zone, fallFirst);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), spring);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), fallFirst);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), fallNext);
    }

    [Fact]
    public async Task Repeated_and_concurrent_due_ticks_share_one_durable_identity()
    {
        var scheduleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var calls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => PostgresSyncWorkQueue.CreateScheduleWorkId(
                "tenant", scheduleId, 4, Noon)))
            .ToArray();

        var ids = await Task.WhenAll(calls);

        Assert.Single(ids.Distinct());
        Assert.NotEqual(
            ids[0],
            PostgresSyncWorkQueue.CreateScheduleWorkId(
                "tenant", scheduleId, 4, Noon.AddMinutes(30)));
    }

    [Fact]
    public void Disabled_or_stale_schedule_does_not_queue_but_unsafe_policy_is_held()
    {
        var policy = Policy(safe: true);
        var schedule = new EntitySyncSchedule(
            "tenant", Guid.NewGuid(), 1, "schedule", policy.PolicyId,
            policy.Version, "*/30 * * * *", "UTC", true, Noon, null,
            Noon.AddHours(-1), new EntitySyncActor("admin"));

        Assert.True(PostgresSyncWorkQueue.CanQueueDue(
            schedule, policy, latestScheduleVersion: 1, Noon));
        Assert.True(PostgresSyncWorkQueue.CanQueueDue(
            schedule, Policy(safe: false), latestScheduleVersion: 1, Noon));
        Assert.False(PostgresSyncWorkQueue.CanQueueDue(
            schedule, policy, latestScheduleVersion: 2, Noon));
        var disabled = schedule.NextVersion(
            schedule.CronExpression, schedule.TimeZone, false, null,
            new EntitySyncActor("admin"), Noon);
        Assert.False(PostgresSyncWorkQueue.CanQueueDue(
            disabled, policy, latestScheduleVersion: 2, Noon));
    }

    [Fact]
    public void Route_lease_contention_waits_until_database_expiry()
    {
        Assert.False(PostgresRouteLock.CanTakeLease(
            Noon.AddSeconds(1), Noon));
        Assert.True(PostgresRouteLock.CanTakeLease(
            Noon, Noon));
    }

    [Fact]
    public async Task Worker_wakes_from_notification_without_waiting_for_fallback()
    {
        var clock = new ManualTimeProvider(Noon);
        var signal = new TestWorkSignal();
        var wait = EntitySyncControlWorker.WaitForWorkAsync(signal, clock, default);

        Assert.False(wait.IsCompleted);
        signal.Notify();

        Assert.Equal(ControlWakeReason.Notification, await wait);
    }

    [Fact]
    public async Task Worker_uses_exact_five_second_fallback_without_sleeping()
    {
        var clock = new ManualTimeProvider(Noon);
        var signal = new TestWorkSignal();
        var wait = EntitySyncControlWorker.WaitForWorkAsync(signal, clock, default);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(wait.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ControlWakeReason.Fallback, await wait);
    }

    [Fact]
    public void Safe_subset_auto_approval_allows_linked_allowlisted_updates_only()
    {
        var policy = Policy(safe: true);
        var allowed = PlanItem("Update", "Linked", ["Name"]);
        var create = PlanItem("Create", "NoMatch", ["Name"]);
        var review = PlanItem("Review", "Review", ["Name"]);
        var blocked = PlanItem("Update", "Linked", ["Secret"]);

        Assert.True(PostgresSyncWorkQueue.IsSafeSubset(policy, [allowed]));
        Assert.False(PostgresSyncWorkQueue.IsSafeSubset(policy, [create]));
        Assert.False(PostgresSyncWorkQueue.IsSafeSubset(policy, [review]));
        Assert.False(PostgresSyncWorkQueue.IsSafeSubset(policy, [blocked]));
    }

    [Fact]
    public void Expired_operation_with_post_dispatch_uncertainty_reconciles_before_retry()
    {
        var safe = OperationItem(EntitySyncItemOutcome.Pending, dispatchStarted: false);
        var uncertain = OperationItem(EntitySyncItemOutcome.Pending, dispatchStarted: true);
        var unknown = OperationItem(EntitySyncItemOutcome.Unknown, dispatchStarted: true);

        Assert.True(PostgresSyncWorkQueue.CanRetryExpiredOperation([safe]));
        Assert.False(PostgresSyncWorkQueue.CanRetryExpiredOperation([uncertain]));
        Assert.False(PostgresSyncWorkQueue.CanRetryExpiredOperation([unknown]));
    }

    [Fact]
    public async Task Retention_scrubs_audit_and_operation_ciphertext_at_database_time()
    {
        var audits = new RetentionAuditRepository();
        var operations = new RetentionOperationRepository();
        var worker = new AuditRetentionWorker(audits, operations, ["tenant"]);

        var result = await worker.RunOnceAsync(default);

        Assert.Equal(4, result);
        Assert.Equal(1, audits.Calls);
        Assert.Equal(1, operations.Calls);
    }

    private static EntitySyncPolicy Policy(bool safe)
    {
        var definition = new EntitySyncPolicyDefinition(
            "OrchestraMSP", "source", "Client", "HaloPSA", "target", "Client",
            false, false, 90, 70, null, null,
            EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
            ["Name"], ["Secret"], safe);
        return EntitySyncPolicy.Create(
            "tenant", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "policy", "client-route", definition, true, Noon,
            new EntitySyncActor("policy-admin"));
    }

    private static EntitySyncDurablePlanItem PlanItem(
        string action, string matchType, IReadOnlyList<string> fields) =>
        new(
            "tenant", Guid.NewGuid(), Guid.NewGuid(), 0,
            "OrchestraMSP", "source", "Client", "source-key", "source-id",
            "HaloPSA", "target", "Client", "target-id", action,
            new EntitySyncMatchEvidence(100, matchType, ["test"]),
            new EntitySyncJsonValue("{}"), new EntitySyncJsonValue("{}"), null,
            new EntitySyncSha256(new string('a', 64)),
            fields.Select(field => new EntityFieldChange(
                field, new EntitySyncJsonValue("null"), new EntitySyncJsonValue("\"value\""),
                new EntitySyncSha256(new string('b', 64)),
                new EntitySyncSha256(new string('c', 64)), false)));

    private static EntitySyncOperationItem OperationItem(
        EntitySyncItemOutcome outcome, bool dispatchStarted) =>
        EntitySyncOperationItem.Rehydrate(
            "tenant", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
            "OrchestraMSP", "source", "Client", "key", "id",
            "HaloPSA", "target", "Client", "target-id", "Update",
            new EntitySyncJsonValue("{}"), new EntitySyncJsonValue("{}"), null,
            new EntitySyncSha256(new string('d', 64)), null, Noon.AddDays(365),
            dispatchStarted ? "request" : null, outcome, null, null, null,
            outcome == EntitySyncItemOutcome.Pending ? null : Noon) with
        { DispatchStartedAt = dispatchStarted ? Noon : null };

    private sealed class MemoryScheduleRepository : ISyncScheduleRepository
    {
        public List<EntitySyncSchedule> Versions { get; } = [];
        public Task InsertVersionAsync(string tenantId, EntitySyncSchedule schedule, CancellationToken cancellationToken)
        {
            if (Versions.Any(value => value.ScheduleId == schedule.ScheduleId && value.Version == schedule.Version))
                throw new InvalidOperationException("duplicate version");
            Versions.Add(schedule);
            return Task.CompletedTask;
        }
        public Task<EntitySyncSchedule?> GetAsync(string tenantId, Guid scheduleId, int version, CancellationToken cancellationToken) =>
            Task.FromResult(Versions.SingleOrDefault(value => value.ScheduleId == scheduleId && value.Version == version));
        public Task<EntitySyncSchedule?> GetLatestAsync(string tenantId, Guid scheduleId, CancellationToken cancellationToken) =>
            Task.FromResult(Versions.Where(value => value.ScheduleId == scheduleId).OrderByDescending(value => value.Version).FirstOrDefault());
        public Task<IReadOnlyList<EntitySyncSchedule>> ListDueAsync(string tenantId, DateTimeOffset dueAt, int maximumRows, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncSchedule>>([]);
        public Task InsertChangeEventAsync(string tenantId, EntitySyncCanonicalChangeEvent changeEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntitySyncCanonicalChangeEvent>> ListPendingChangeEventsAsync(string tenantId, int maximumRows, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TrySetChangeEventStatusAsync(string tenantId, Guid eventId, EntitySyncCanonicalChangeStatus expectedStatus, EntitySyncCanonicalChangeStatus status, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SchedulePolicyRepository(EntitySyncPolicy policy) : ISyncPolicyRepository
    {
        public EntitySyncPolicy Policy { get; } = policy;
        public Task InsertAsync(string tenantId, EntitySyncPolicy policy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryInsertValidatedAsync(string tenantId, EntitySyncPolicy policy, string sourceConnectionId, long sourceGeneration, string targetConnectionId, long targetGeneration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryInsertValidatedWithTokenAsync(string tenantId, EntitySyncPolicy policy, string sourceConnectionId, long sourceGeneration, string targetConnectionId, long targetGeneration, string idempotencyToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncPolicy?> GetByIdempotencyTokenAsync(string tenantId, Guid policyId, string idempotencyToken, CancellationToken cancellationToken) => Task.FromResult<EntitySyncPolicy?>(null);
        public Task<EntitySyncPolicy?> GetAsync(string tenantId, Guid policyId, int version, CancellationToken cancellationToken) => Task.FromResult<EntitySyncPolicy?>(Policy.PolicyId == policyId && Policy.Version == version ? Policy : null);
        public Task<EntitySyncPolicy?> GetLatestAsync(string tenantId, Guid policyId, CancellationToken cancellationToken) => Task.FromResult<EntitySyncPolicy?>(Policy.PolicyId == policyId ? Policy : null);
        public Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(string tenantId, string? routeScope, bool? enabled, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EntitySyncPolicy>>([Policy]);
    }

    private sealed class TestWorkSignal : IEntitySyncWorkSignal
    {
        private TaskCompletionSource wake = NewSource();
        public Task WaitAsync(CancellationToken cancellationToken) => wake.Task.WaitAsync(cancellationToken);
        public Task NotifyAsync(CancellationToken cancellationToken)
        {
            Notify();
            return Task.CompletedTask;
        }
        public void Notify()
        {
            var current = Interlocked.Exchange(ref wake, NewSource());
            current.TrySetResult();
        }
        private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RetentionAuditRepository : ISyncAuditRepository
    {
        public int Calls { get; private set; }
        public Task AppendAsync(string tenantId, EntitySyncAuditEvent auditEvent, EntitySyncAuditEventFullValues? fullValues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncAuditPage> ListAsync(string tenantId, DateTimeOffset? continuationOccurredAt, Guid? continuationEventId, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncAuditEventFullValues?> GetFullValuesAsync(string tenantId, Guid auditEventId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> DeleteExpiredFullValuesAsync(string tenantId, DateTimeOffset now, int maximumRows, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(2);
        }
    }

    private sealed class RetentionOperationRepository : ISyncOperationRepository
    {
        public int Calls { get; private set; }
        public Task<int> DeleteExpiredSnapshotsAsync(string tenantId, DateTimeOffset now, int maximumRows, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(2);
        }
        public Task InsertAsync(string tenantId, EntitySyncOperation operation, IReadOnlyList<EntitySyncOperationItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryInsertAsync(string tenantId, EntitySyncOperation operation, IReadOnlyList<EntitySyncOperationItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperation?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperation?> GetAsync(string tenantId, Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperationPage> ListPageAsync(string tenantId, EntitySyncOperationListCursor? cursor, int maximumRows, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntitySyncOperationItem>> GetItemsAsync(string tenantId, Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperationItem?> GetItemAsync(string tenantId, Guid operationId, Guid itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperation?> TryLeaseNextAsync(string tenantId, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryReplaceAsync(string tenantId, Guid operationId, EntitySyncOperationStatus expectedStatus, EntitySyncOperation replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryReplaceItemAsync(string tenantId, Guid operationId, Guid planId, Guid itemId, int expectedOperationAttempt, string leaseOwner, DateTimeOffset now, EntitySyncItemOutcome expectedOutcome, EntitySyncOperationItem replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DispatchPreparationResult> TryPrepareDispatchAsync(string tenantId, Guid operationId, Guid planId, Guid itemId, int expectedOperationAttempt, string leaseOwner, Guid policyId, int policyVersion, EntitySyncSha256 policyDefinitionSha256, EntitySyncOperationItem preparedItem, EntitySyncOperationItemSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryRecordItemAsync(string tenantId, Guid operationId, Guid planId, Guid itemId, int expectedOperationAttempt, string leaseOwner, EntitySyncItemOutcome expectedOutcome, EntitySyncOperationItem replacement, EntitySyncOperationItemSnapshot? snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperation?> TryFinalizeAttemptAsync(string tenantId, Guid operationId, int expectedOperationAttempt, string leaseOwner, DateTimeOffset completedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperation?> TryCancelAttemptAsync(string tenantId, Guid operationId, int expectedOperationAttempt, string leaseOwner, DateTimeOffset completedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UnknownItemLease?> TryLeaseUnknownItemAsync(string tenantId, Guid operationId, Guid itemId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryRenewUnknownItemLeaseAsync(string tenantId, Guid operationId, Guid itemId, int expectedReconciliationAttempt, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryRecordReconciliationEvidenceAsync(string tenantId, Guid operationId, Guid itemId, int expectedReconciliationAttempt, string leaseOwner, EntitySyncSha256 afterPayloadSha256, string? vendorTargetEntityId, EntitySyncOperationItemSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryCompleteReconciliationAsync(string tenantId, Guid operationId, Guid itemId, int expectedReconciliationAttempt, string leaseOwner, EntitySyncOperationItem replacement, EntitySyncOperationItemSnapshot? snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryCommitReconciliationSuccessAsync(string tenantId, Guid operationId, Guid itemId, int expectedReconciliationAttempt, string reconciliationLeaseOwner, EntitySyncOperationItem replacement, EntitySyncChangeState? checkpoint, EntitySyncAuditEvent auditEvent, EntitySyncAuditEventFullValues? auditFullValues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertSnapshotAsync(string tenantId, EntitySyncOperationItemSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntitySyncOperationItemSnapshot?> GetSnapshotAsync(string tenantId, Guid operationId, Guid itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private readonly object sync = new();
        private DateTimeOffset now = initial;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
        public void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (sync)
            {
                now += amount;
                due = timers.Where(timer => timer.DueAt <= now).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, GetUtcNow() + dueTime);
            lock (sync) timers.Add(timer);
            return timer;
        }
        private void Remove(ManualTimer timer) { lock (sync) timers.Remove(timer); }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            private int disposed;
            public DateTimeOffset DueAt { get; } = dueAt;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Fire() { if (Interlocked.Exchange(ref disposed, 1) == 0) { owner.Remove(this); callback(state); } }
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
