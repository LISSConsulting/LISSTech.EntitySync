using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ChangedOnlyPlanningTests
{
    private const string Scope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task FirstChangedOnlyPlanUpdatesLinkedEntityAndCarriesExactMappedHash()
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);

        var plan = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("Update", item.Action);
        Assert.Equal("Linked", item.MatchType);
        Assert.Equal(EntityWriteRequestDigest.SchemaVersion, item.DesiredStateHashVersion);
        Assert.Matches("^[0-9a-f]{64}$", item.DesiredStateHash!);
        Assert.Equal(
            EntityWriteRequestDigest.Compute(fixture.Mapper.MapUpdate(item.Source, item.Target!, plan.Execution.MatchOptions)),
            item.DesiredStateHash);
        Assert.Equal(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, plan.Execution.UpdatePolicy);
        Assert.Equal(Scope, plan.Execution.ChangeStateScope);
    }

    [Fact]
    public async Task IdenticalCheckpointProducesUnchangedNoAction()
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);
        var first = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);
        await fixture.ChangeStates.UpsertAsync(fixture.State(Assert.Single(first.Items)), default);

        var second = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        var item = Assert.Single(second.Items);
        Assert.Equal("None", item.Action);
        Assert.Equal("Unchanged", item.MatchType);
        Assert.Contains("Mapped update payload matches the last successful synchronization.", item.Reasons);
        Assert.Equal(first.Items[0].DesiredStateHash, item.DesiredStateHash);
    }

    [Fact]
    public async Task MappedFieldChangeProducesUpdateWithNewHash()
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);
        var first = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);
        var firstItem = Assert.Single(first.Items);
        var firstHash = firstItem.DesiredStateHash;
        await fixture.ChangeStates.UpsertAsync(fixture.State(firstItem), default);
        fixture.Sources[0].Email = "changed@example.com";

        var second = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        var item = Assert.Single(second.Items);
        Assert.Equal("Update", item.Action);
        Assert.Equal("Linked", item.MatchType);
        Assert.NotEqual(firstHash, item.DesiredStateHash);
    }

    [Fact]
    public async Task TargetIdChangeProducesUpdate()
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);
        var first = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);
        var firstItem = Assert.Single(first.Items);
        await fixture.ChangeStates.UpsertAsync(fixture.State(firstItem), default);
        fixture.Targets[0].Id = "8";

        var second = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        var item = Assert.Single(second.Items);
        Assert.Equal("Update", item.Action);
        Assert.Equal("8", item.Target!.Id);
        Assert.NotEqual(firstItem.DesiredStateHash, item.DesiredStateHash);
    }

    [Fact]
    public async Task HashVersionMismatchProducesUpdate()
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);
        var first = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);
        var firstItem = Assert.Single(first.Items);
        await fixture.ChangeStates.UpsertAsync(
            fixture.State(firstItem) with { HashVersion = EntityWriteRequestDigest.SchemaVersion + 1 },
            default);

        var second = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        var item = Assert.Single(second.Items);
        Assert.Equal("Update", item.Action);
        Assert.Equal(firstItem.DesiredStateHash, item.DesiredStateHash);
    }

    [Fact]
    public async Task HighConfidenceNameOnlyMatchDoesNotWrite()
    {
        using var fixture = Fixture([Source("42", "Acme")], [Target("7", "Acme")]);

        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, autoLinkScore: 70),
            default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("None", item.Action);
        Assert.Equal("HighConfidence", item.MatchType);
        Assert.Contains("Recurring changed-only sync permits persistently linked updates only.", item.Reasons);
        Assert.Null(item.DesiredStateHash);
    }

    [Fact]
    public async Task AmbiguousMatchDoesNotWrite()
    {
        using var fixture = Fixture([Source("42", "Acme")], [Target("7", "Acme"), Target("8", "Acme")]);

        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, autoLinkScore: 70),
            default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("None", item.Action);
        Assert.Equal("Ambiguous", item.MatchType);
        Assert.Null(item.Target);
        Assert.Null(item.DesiredStateHash);
    }

    [Fact]
    public async Task MissingTargetDoesNotCreate()
    {
        using var fixture = Fixture([Source("42", "Acme")], []);

        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, createMissing: true),
            default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("None", item.Action);
        Assert.Equal("NoMatch", item.MatchType);
        Assert.Null(item.Target);
        Assert.Null(item.DesiredStateHash);
    }

    [Fact]
    public async Task UnmatchedSourceDoesNotCreate()
    {
        var source = Source("42", "Acme");
        source.ExternalIds.Clear();
        using var fixture = Fixture([source], [Target("7", "Entirely Different")]);

        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, createMissing: true),
            default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("None", item.Action);
        Assert.NotEqual("Linked", item.MatchType);
        Assert.Null(item.DesiredStateHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public async Task ChangedOnlyPlanRejectsMissingOrInvalidChangeStateScope(string? scope)
    {
        using var fixture = Fixture([Source("42", "Acme")], [LinkedTarget("7", "42", "Acme")]);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, scope),
            default));

        Assert.Contains("lowercase 64-character hexadecimal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ChangeStates.GetCalls);
    }

    [Fact]
    public async Task ChangedOnlyPlanLoadsAllSourceStatesInOneBatch()
    {
        using var fixture = Fixture(
            [Source("42", "Acme"), Source("43", "Beta")],
            [LinkedTarget("7", "42", "Acme"), LinkedTarget("8", "43", "Beta")]);

        await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

        Assert.Equal(1, fixture.ChangeStates.GetCalls);
        Assert.Equal(["42", "43"], fixture.ChangeStates.LastSourceIds!.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(fixture.Route, fixture.ChangeStates.LastRoute);
    }

    [Fact]
    public async Task StandardPlanPreservesNameOnlyLinkBehaviorWithoutLoadingChangeState()
    {
        using var fixture = Fixture([Source("42", "Acme")], [Target("7", "Acme")]);

        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(EntitySyncUpdatePolicy.Standard, scope: null, autoLinkScore: 70),
            default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("Link", item.Action);
        Assert.Equal("HighConfidence", item.MatchType);
        Assert.Null(item.DesiredStateHash);
        Assert.Equal(EntitySyncUpdatePolicy.Standard, plan.Execution.UpdatePolicy);
        Assert.Null(plan.Execution.ChangeStateScope);
        Assert.Equal(0, fixture.ChangeStates.GetCalls);
    }

    private static PlanningFixture Fixture(IReadOnlyList<ExternalEntity> sources, IReadOnlyList<ExternalEntity> targets) =>
        new(sources, targets);

    private static ExternalEntity Source(string id, string name) => new()
    {
        Vendor = "NetSuite",
        EntityType = "Customer",
        Id = id,
        Name = name,
        ExternalIds = { ["NetSuiteInternalId"] = id }
    };

    private static ExternalEntity LinkedTarget(string id, string sourceId, string name)
    {
        var target = Target(id, name);
        target.CustomFields["CFNetSuiteCustomerID"] = sourceId;
        return target;
    }

    private static ExternalEntity Target(string id, string name) => new()
    {
        Vendor = "HaloPSA",
        EntityType = "Client",
        Id = id,
        Name = name
    };

    private sealed class PlanningFixture : IDisposable
    {
        private readonly InMemoryEntityConnectionRepository connections = new();
        private readonly TestEntitySyncPlanRepository plans = new();
        private readonly InMemoryEntityExclusionRepository exclusions = new();

        public PlanningFixture(IReadOnlyList<ExternalEntity> sources, IReadOnlyList<ExternalEntity> targets)
        {
            Sources = sources.ToList();
            Targets = targets.ToList();
            connections.Register("tenant", "source", new TestAdapter("NetSuite", Sources));
            connections.Register("tenant", "target", new TestAdapter("HaloPSA", Targets));
            Mapper = new DefaultEntityMapper();
            ChangeStates = new RecordingChangeStateRepository();
            Service = new EntitySyncService(
                new EntitySyncPlanner(
                    connections,
                    plans,
                    exclusions,
                    new WeightedEntityMatcher(),
                    Mapper,
                    ChangeStates),
                connections,
                plans,
                exclusions,
                Mapper,
                ChangeStates,
                TimeProvider.System);
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

        public List<ExternalEntity> Sources { get; }
        public List<ExternalEntity> Targets { get; }
        public DefaultEntityMapper Mapper { get; }
        public RecordingChangeStateRepository ChangeStates { get; }
        public EntitySyncService Service { get; }
        public EntitySyncChangeStateRoute Route { get; }

        public CreateEntitySyncPlanRequest Request(
            EntitySyncUpdatePolicy policy,
            string? scope = Scope,
            int autoLinkScore = 85,
            bool createMissing = false) => new()
            {
                TenantId = "tenant",
                SourceVendor = "NetSuite",
                SourceConnectionId = "source",
                SourceEntityType = "Customer",
                TargetVendor = "HaloPSA",
                TargetConnectionId = "target",
                TargetEntityType = "Client",
                AutoLinkScore = autoLinkScore,
                CreateMissing = createMissing,
                UpdatePolicy = policy,
                ChangeStateScope = scope
            };

        public EntitySyncChangeState State(EntitySyncPlanItem item) => new(
            Route,
            item.Source.Id,
            item.Source.Name,
            item.Target?.Id ?? throw new InvalidOperationException("Target is required."),
            item.DesiredStateHashVersion ?? throw new InvalidOperationException("Hash version is required."),
            item.DesiredStateHash ?? throw new InvalidOperationException("Payload hash is required."),
            DateTimeOffset.UtcNow);

        public void Dispose() => connections.Dispose();
    }

    private sealed class RecordingChangeStateRepository : IEntitySyncChangeStateRepository
    {
        private readonly InMemoryEntitySyncChangeStateRepository inner = new();

        public int GetCalls { get; private set; }
        public EntitySyncChangeStateRoute? LastRoute { get; private set; }
        public IReadOnlyCollection<string>? LastSourceIds { get; private set; }

        public Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
            EntitySyncChangeStateRoute route,
            IReadOnlyCollection<string> sourceEntityIds,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            LastRoute = route;
            LastSourceIds = sourceEntityIds.ToArray();
            return inner.GetBySourceIdsAsync(route, sourceEntityIds, cancellationToken);
        }

        public Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken) =>
            inner.UpsertAsync(state, cancellationToken);
    }

    private sealed class TestAdapter(string vendor, IReadOnlyList<ExternalEntity> entities) : IEntityAdapter
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
            throw new NotSupportedException();

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
