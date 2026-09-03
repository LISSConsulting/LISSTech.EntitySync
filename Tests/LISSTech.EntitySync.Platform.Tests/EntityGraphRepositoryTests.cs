using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntityGraphRepositoryTests
{
    [Fact]
    public async Task EntityObservationKeepsLatestSnapshotWhenOlderDataArrivesLate()
    {
        var repository = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var older = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);

        await repository.ObserveEntitiesAsync(
            new EntityGraphObservation(scope, [Entity("NetSuite", "Customer", "42", "Current Name")], newer, "new-plan"),
            default);
        await repository.ObserveEntitiesAsync(
            new EntityGraphObservation(scope, [Entity("NetSuite", "Customer", "42", "Stale Name")], older, "old-plan"),
            default);

        var record = Assert.Single(await repository.QueryEntitiesAsync(new EntityGraphQuery("tenant"), default));
        Assert.Equal("Current Name", record.Entity.Name);
        Assert.Equal(older, record.FirstObservedAt);
        Assert.Equal(newer, record.LastObservedAt);
        Assert.Equal("new-plan", record.LastPlanId);
    }

    [Fact]
    public async Task ConfirmedRelationshipIsNotDowngradedByLaterProposal()
    {
        var repository = new InMemoryEntityGraphRepository();
        var observedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var sourceScope = new EntityGraphScope("tenant", "NetSuite", "source", "Customer");
        var targetScope = new EntityGraphScope("tenant", "HaloPSA", "target", "Client");
        await repository.ObserveEntitiesAsync(
            new EntityGraphObservation(sourceScope, [Entity("NetSuite", "Customer", "42", "Acme")], observedAt),
            default);
        await repository.ObserveEntitiesAsync(
            new EntityGraphObservation(targetScope, [Entity("HaloPSA", "Client", "7", "Acme")], observedAt),
            default);
        var source = new EntityGraphNodeKey("tenant", "NetSuite", "source", "Customer", "42");
        var target = new EntityGraphNodeKey("tenant", "HaloPSA", "target", "Client", "7");

        await repository.ObserveRelationshipsAsync(
            [Relationship(source, target, EntityGraphRelationshipStatuses.Confirmed, "Applied", 100, observedAt, "confirmed-plan")],
            default);
        await repository.ObserveRelationshipsAsync(
            [Relationship(source, target, EntityGraphRelationshipStatuses.Proposed, "HighConfidence", 85, observedAt.AddMinutes(1), "later-plan")],
            default);

        var relationship = Assert.Single(await repository.QueryRelationshipsAsync(
            new EntityGraphRelationshipQuery(target),
            default));
        Assert.Equal(EntityGraphRelationshipStatuses.Confirmed, relationship.Status);
        Assert.Equal("Applied", relationship.MatchType);
        Assert.Equal(100, relationship.Score);
        Assert.Equal(["Confirmed evidence"], relationship.Evidence);
        Assert.Equal(observedAt, relationship.ConfirmedAt);
        Assert.Equal(observedAt.AddMinutes(1), relationship.LastObservedAt);
        Assert.Equal("later-plan", relationship.LastPlanId);
    }

    [Fact]
    public async Task PlanningRetainsVendorRecordsAndApplyConfirmsRelationship()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var exclusions = new InMemoryEntityExclusionRepository();
        var changeStates = new InMemoryEntitySyncChangeStateRepository();
        var graph = new InMemoryEntityGraphRepository();
        var mapper = new DefaultEntityMapper();
        connections.Register("tenant", "source", new TestAdapter("NetSuite", [Entity("NetSuite", "Customer", "42", "Acme")]));
        connections.Register("tenant", "target", new TestAdapter("HaloPSA", [Entity("HaloPSA", "Client", "7", "Acme")]));
        var service = new EntitySyncService(
            new EntitySyncPlanner(
                connections,
                plans,
                exclusions,
                new WeightedEntityMatcher(),
                mapper,
                changeStates,
                graph),
            connections,
            plans,
            exclusions,
            mapper,
            changeStates,
            graph,
            TimeProvider.System);

        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "NetSuite",
            SourceConnectionId = "source",
            SourceEntityType = "Customer",
            TargetVendor = "HaloPSA",
            TargetConnectionId = "target",
            TargetEntityType = "Client",
            AutoLinkScore = 70,
            ReviewScore = 60
        }, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("Link", item.Action);
        var records = await graph.QueryEntitiesAsync(new EntityGraphQuery("tenant"), default);
        Assert.Equal(2, records.Count);
        Assert.Equal(["HaloPSA", "NetSuite"], records.Select(record => record.Key.Vendor).Order(StringComparer.Ordinal).ToArray());
        var source = new EntityGraphNodeKey("tenant", "NetSuite", "source", "Customer", "42");
        var relationship = Assert.Single(await graph.QueryRelationshipsAsync(new EntityGraphRelationshipQuery(source), default));
        Assert.Equal(EntityGraphRelationshipStatuses.Proposed, relationship.Status);
        Assert.Equal(plan.Id, relationship.LastPlanId);

        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        var result = await service.ApplyAsync("tenant", plan.Id, true, default);

        Assert.True(result.Success);
        relationship = Assert.Single(await graph.QueryRelationshipsAsync(new EntityGraphRelationshipQuery(source), default));
        Assert.Equal(EntityGraphRelationshipStatuses.Confirmed, relationship.Status);
        Assert.Equal(plan.Id, relationship.LastPlanId);
        Assert.NotNull(relationship.ConfirmedAt);
    }

    [Fact]
    public async Task TargetOnlyDeleteTombstonesRecordWithoutInventingRelationship()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var exclusions = new InMemoryEntityExclusionRepository();
        var changeStates = new InMemoryEntitySyncChangeStateRepository();
        var graph = new InMemoryEntityGraphRepository();
        var mapper = new DefaultEntityMapper();
        connections.Register("tenant", "source", new TestAdapter(
            "HaloPSA",
            [Entity("HaloPSA", "Client", "42", "Acme")]));
        connections.Register("tenant", "target", new TestAdapter(
            "BillCom",
            [
                Entity("BillCom", "Client", "7", "Acme"),
                Entity("BillCom", "Client", "8", "Orphan")
            ]));
        var service = new EntitySyncService(
            new EntitySyncPlanner(
                connections,
                plans,
                exclusions,
                new WeightedEntityMatcher(),
                mapper,
                changeStates,
                graph),
            connections,
            plans,
            exclusions,
            mapper,
            changeStates,
            graph,
            TimeProvider.System);

        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "source",
            SourceEntityType = "Client",
            TargetVendor = "BillCom",
            TargetConnectionId = "target",
            TargetEntityType = "Client",
            AutoLinkScore = 70,
            ReviewScore = 60
        }, default);
        Assert.Contains(plan.Items, item => item.Action == "Delete" && item.Target?.Id == "8");
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        var result = await service.ApplyAsync("tenant", plan.Id, true, default);
        Assert.True(result.Success);

        var retainedTargets = await graph.QueryEntitiesAsync(
            new EntityGraphQuery("tenant"),
            default);
        var orphan = Assert.Single(retainedTargets, record => record.Key.EntityId == "8");
        Assert.False(orphan.Entity.IsActive);
        Assert.Equal("RemovedProjection", orphan.Entity.GetCustomField("EntitySyncRecordState"));
        Assert.Empty(await graph.QueryRelationshipsAsync(
            new EntityGraphRelationshipQuery(orphan.Key),
            default));
    }

    [Fact]
    public void EntityGraphMigrationCreatesCurrentHistoryAndRelationshipTables()
    {
        var assembly = typeof(PostgresEntityGraphRepository).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("004_entity_graph.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var migration = reader.ReadToEnd();

        Assert.Contains("CREATE TABLE entitysync.entity_records", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE entitysync.entity_record_versions", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE entitysync.entity_relationships", migration, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (", migration, StringComparison.Ordinal);
        Assert.Contains("REFERENCES entitysync.entity_records", migration, StringComparison.Ordinal);
    }

    private static ExternalEntity Entity(string vendor, string entityType, string id, string name) => new()
    {
        Vendor = vendor,
        EntityType = entityType,
        Id = id,
        Name = name,
        ExternalIds = { [vendor + "Id"] = id }
    };

    private static EntityGraphRelationshipObservation Relationship(
        EntityGraphNodeKey source,
        EntityGraphNodeKey target,
        string status,
        string matchType,
        int score,
        DateTimeOffset observedAt,
        string planId) => new(
            source,
            target,
            EntityGraphRelationshipTypes.EquivalentTo,
            status,
            matchType,
            score,
            [status == EntityGraphRelationshipStatuses.Confirmed ? "Confirmed evidence" : "Proposed evidence"],
            observedAt,
            planId);

    private sealed class TestAdapter(string vendor, IReadOnlyList<ExternalEntity> entities) : IEntityAdapter, IEntityDeleteAdapter
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(entities);
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = "created",
                Action = "Create",
                Success = true
            });

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = request.Id,
                Action = "Update",
                Success = true
            });

        public Task<EntityWriteResult> DeleteEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = request.Id,
                Action = "Delete",
                Success = true
            });

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}