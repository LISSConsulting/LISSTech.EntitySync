using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ChangedOnlyApplyTests
{
    private const string Scope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset AppliedAt = new(2026, 8, 27, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulChangedOnlyUpdateCheckpointsDesiredHashOnceBeforeSuccessProgress()
    {
        using var fixture = await ApprovedFixtureAsync();
        var progress = new List<EntitySyncApplyProgress>();

        var result = await fixture.Service.ApplyAsync(
            "tenant",
            fixture.Plan.Id,
            true,
            default,
            item =>
            {
                fixture.Events.Add("progress");
                progress.Add(item);
            });
        var stored = await fixture.ChangeStates.GetBySourceIdsAsync(fixture.Route, ["42"], default);

        Assert.True(result.Success);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.True(Assert.Single(result.Results).Success);
        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(1, fixture.ChangeStates.UpsertCalls);
        Assert.Equal(["write", "checkpoint", "progress"], fixture.Events);
        var checkpoint = stored["42"];
        Assert.Equal(fixture.Route, checkpoint.Route);
        Assert.Equal("42", checkpoint.SourceEntityId);
        Assert.Equal("Acme", checkpoint.SourceName);
        Assert.Equal("7", checkpoint.TargetEntityId);
        Assert.Equal(fixture.Plan.Items[0].DesiredStateHashVersion, checkpoint.HashVersion);
        Assert.Equal(fixture.Plan.Items[0].DesiredStateHash, checkpoint.PayloadHash);
        Assert.Equal(AppliedAt, checkpoint.AppliedAt);
        var finalProgress = Assert.Single(progress);
        Assert.Equal(1, finalProgress.Processed);
        Assert.Equal(1, finalProgress.Succeeded);
        Assert.Equal(0, finalProgress.Failed);
        Assert.Equal(0, finalProgress.Skipped);
    }

    [Fact]
    public async Task CheckpointFailureAfterSuccessfulWriteFailsOneItemWithoutClaimingWriteFailure()
    {
        using var fixture = await ApprovedFixtureAsync(
            checkpoint: static (_, _) => Task.FromException(new InvalidOperationException("db unavailable")));
        var progress = new List<EntitySyncApplyProgress>();

        var result = await fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default, progress.Add);

        Assert.False(result.Success);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(1, fixture.ChangeStates.UpsertCalls);
        var item = Assert.Single(result.Results);
        Assert.False(item.Success);
        Assert.Equal("7", item.Id);
        Assert.Equal("Target write succeeded, but change-state checkpoint failed.", item.Message);
        Assert.DoesNotContain("Target write failed", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db unavailable", item.Message, StringComparison.OrdinalIgnoreCase);
        var finalProgress = Assert.Single(progress);
        Assert.Equal(1, finalProgress.Processed);
        Assert.Equal(0, finalProgress.Succeeded);
        Assert.Equal(1, finalProgress.Failed);
        Assert.Equal(0, finalProgress.Skipped);
        Assert.Equal(EntitySyncPlanStatuses.Failed, fixture.Status);
    }

    [Fact]
    public async Task FailedTargetWriteDoesNotCheckpoint()
    {
        using var fixture = await ApprovedFixtureAsync(
            update: static _ => Task.FromResult(WriteResult(success: false)));

        var result = await fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default);

        Assert.False(result.Success);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(0, fixture.ChangeStates.UpsertCalls);
        Assert.Equal("Target write failed.", Assert.Single(result.Results).Message);
    }

    [Fact]
    public async Task CancelledTargetWritePropagatesCancellationWithoutCheckpointing()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = await ApprovedFixtureAsync(update: token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(WriteResult(success: true));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, cancellation.Token));

        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(0, fixture.ChangeStates.UpsertCalls);
        Assert.Equal(EntitySyncPlanStatuses.Failed, fixture.Status);
    }

    [Fact]
    public async Task CancelledCheckpointPropagatesCancellationWithoutRecordingFailedProgress()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = await ApprovedFixtureAsync(checkpoint: (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var progress = new List<EntitySyncApplyProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, cancellation.Token, progress.Add));

        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(1, fixture.ChangeStates.UpsertCalls);
        Assert.Empty(progress);
        Assert.Equal(EntitySyncPlanStatuses.Failed, fixture.Status);
    }

    [Fact]
    public async Task StandardPlanPreservesApplyBehaviorWithoutReadingOrWritingChangeState()
    {
        using var fixture = await ApprovedFixtureAsync(policy: EntitySyncUpdatePolicy.Standard);

        var result = await fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default);

        Assert.True(result.Success);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, fixture.Target.UpdateCalls);
        Assert.Equal(0, fixture.ChangeStates.GetCalls);
        Assert.Equal(0, fixture.ChangeStates.UpsertCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChangedOnlyApplyWithMissingHashMetadataFailsClosedBeforeTargetWrite(
        bool removeHash,
        bool removeVersion)
    {
        using var fixture = await ApprovedFixtureAsync(mutatePlan: plan =>
        {
            var item = Assert.Single(plan.Items);
            if (removeHash) item.DesiredStateHash = null;
            if (removeVersion) item.DesiredStateHashVersion = null;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default));

        Assert.Contains("checkpoint metadata", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Target.UpdateCalls);
        Assert.Equal(0, fixture.ChangeStates.UpsertCalls);
        Assert.Equal(EntitySyncPlanStatuses.Approved, fixture.Status);
    }

    [Fact]
    public async Task ChangedOnlyApplyWithInvalidExecutionScopeFailsClosedBeforeTargetWrite()
    {
        using var fixture = await ApprovedFixtureAsync(
            mutatePlan: plan => plan.Execution.ChangeStateScope = "invalid");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default));

        Assert.Contains("lowercase 64-character hexadecimal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Target.UpdateCalls);
        Assert.Equal(0, fixture.ChangeStates.UpsertCalls);
        Assert.Equal(EntitySyncPlanStatuses.Approved, fixture.Status);
    }

    private static async Task<ApplyFixture> ApprovedFixtureAsync(
        EntitySyncUpdatePolicy policy = EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
        Func<CancellationToken, Task<EntityWriteResult>>? update = null,
        Func<EntitySyncChangeState, CancellationToken, Task>? checkpoint = null,
        Action<EntitySyncPlan>? mutatePlan = null)
    {
        var fixture = new ApplyFixture(update, checkpoint, mutatePlan);
        try
        {
            fixture.Plan = await fixture.Service.CreatePlanAsync(new CreateEntitySyncPlanRequest
            {
                TenantId = "tenant",
                SourceVendor = "NetSuite",
                SourceConnectionId = "source",
                SourceEntityType = "Customer",
                TargetVendor = "HaloPSA",
                TargetConnectionId = "target",
                TargetEntityType = "Client",
                UpdatePolicy = policy,
                ChangeStateScope = policy == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly ? Scope : null
            }, default);
            var inspected = fixture.Service.GetPlan("tenant", fixture.Plan.Id);
            fixture.Service.ApprovePlan("tenant", fixture.Plan.Id, inspected.Digest);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static EntityWriteResult WriteResult(bool success) => new()
    {
        Vendor = "HaloPSA",
        EntityType = "Client",
        Id = "7",
        Action = "Update",
        Success = success,
        Message = success ? "Updated." : "Vendor rejected the update."
    };

    private static ExternalEntity Source() => new()
    {
        Vendor = "NetSuite",
        EntityType = "Customer",
        Id = "42",
        Name = "Acme",
        ExternalIds = { ["NetSuiteInternalId"] = "42" }
    };

    private static ExternalEntity Target() => new()
    {
        Vendor = "HaloPSA",
        EntityType = "Client",
        Id = "7",
        Name = "Acme",
        CustomFields = { ["CFNetSuiteCustomerID"] = "42" }
    };

    private sealed class ApplyFixture : IDisposable
    {
        private readonly InMemoryEntityConnectionRepository connections = new();
        private readonly MutatingPlanRepository plans;

        public ApplyFixture(
            Func<CancellationToken, Task<EntityWriteResult>>? update,
            Func<EntitySyncChangeState, CancellationToken, Task>? checkpoint,
            Action<EntitySyncPlan>? mutatePlan)
        {
            plans = new MutatingPlanRepository(mutatePlan);
            Events = [];
            ChangeStates = new RecordingChangeStateRepository(Events, checkpoint);
            var mapper = new DefaultEntityMapper();
            var graph = new InMemoryEntityGraphRepository();
            Target = new TestAdapter("HaloPSA", [ChangedOnlyApplyTests.Target()], update, Events);
            connections.Register("tenant", "source", new TestAdapter("NetSuite", [Source()]));
            connections.Register("tenant", "target", Target);
            Service = new EntitySyncService(
                new EntitySyncPlanner(
                    connections,
                    plans,
                    new InMemoryEntityExclusionRepository(),
                    new WeightedEntityMatcher(),
                    mapper,
                    ChangeStates,
                    graph),
                connections,
                plans,
                new InMemoryEntityExclusionRepository(),
                mapper,
                ChangeStates,
                graph,
                new FixedTimeProvider(AppliedAt));
            Route = EntitySyncChangeStateRoute.Create(
                "tenant",
                Scope,
                "NetSuite",
                "source",
                "Customer",
                "HaloPSA",
                "target",
                "Client");
        }

        public List<string> Events { get; }
        public RecordingChangeStateRepository ChangeStates { get; }
        public TestAdapter Target { get; }
        public EntitySyncService Service { get; }
        public EntitySyncChangeStateRoute Route { get; }
        public EntitySyncPlan Plan { get; set; } = null!;
        public string Status => plans.Get("tenant", Plan.Id).Status;

        public void Dispose() => connections.Dispose();
    }

    private sealed class MutatingPlanRepository(Action<EntitySyncPlan>? mutatePlan) : IEntitySyncPlanRepository
    {
        private readonly InMemoryEntitySyncPlanRepository inner = new();

        public void Add(EntitySyncPlan plan)
        {
            mutatePlan?.Invoke(plan);
            inner.Add(plan);
        }

        public EntitySyncPlan Get(string tenantId, string planId) => inner.Get(tenantId, planId);

        public void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count) =>
            inner.RecordInspection(tenantId, planId, digest, startIndex, count);

        public bool TryApprove(string tenantId, string planId, string digest) =>
            inner.TryApprove(tenantId, planId, digest);

        public bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus) =>
            inner.TryTransition(tenantId, planId, expectedStatus, newStatus);
    }

    private sealed class RecordingChangeStateRepository(
        List<string> events,
        Func<EntitySyncChangeState, CancellationToken, Task>? checkpoint) : IEntitySyncChangeStateRepository
    {
        private readonly InMemoryEntitySyncChangeStateRepository inner = new();

        public int GetCalls { get; private set; }
        public int UpsertCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
            EntitySyncChangeStateRoute route,
            IReadOnlyCollection<string> sourceEntityIds,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            return inner.GetBySourceIdsAsync(route, sourceEntityIds, cancellationToken);
        }

        public async Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken)
        {
            UpsertCalls++;
            events.Add("checkpoint");
            if (checkpoint is not null)
            {
                await checkpoint(state, cancellationToken);
                return;
            }

            await inner.UpsertAsync(state, cancellationToken);
        }
    }

    private sealed class TestAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity> entities,
        Func<CancellationToken, Task<EntityWriteResult>>? update = null,
        List<string>? events = null) : IEntityAdapter
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int UpdateCalls { get; private set; }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(entities);
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            events?.Add("write");
            return update?.Invoke(cancellationToken) ?? Task.FromResult(WriteResult(success: true));
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
