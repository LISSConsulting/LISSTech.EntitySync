using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class SyncPolicyServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly EntitySyncActor Actor = new("policy-operator");

    [Fact]
    public async Task Create_persists_a_deterministic_hash_and_next_version_adds_a_row()
    {
        var fixture = Fixture.Create();
        var first = await fixture.Service.CreateAsync(
            "tenant", Request(Definition(allowed: ["Phone", "Name"])), Actor, default);
        var sameDefinitionDifferentSetOrder = Definition(allowed: ["Name", "Phone"]);
        var second = await fixture.Service.CreateNextVersionAsync(
            "tenant", first.PolicyId, first.Version, sameDefinitionDifferentSetOrder, null, Actor, default);

        Assert.Equal(first.DefinitionSha256, second.DefinitionSha256);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, fixture.Policies.RowCount);
        Assert.Equal(first, await fixture.Service.GetVersionAsync("tenant", first.PolicyId, 1, default));
        Assert.Equal(second, await fixture.Service.GetVersionAsync("tenant", first.PolicyId, 2, default));
    }

    [Fact]
    public async Task Policy_validation_enforces_score_bounds_order_and_disjoint_fields()
    {
        var fixture = Fixture.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(autoLink: 101));
        Assert.Throws<ArgumentException>(() => Definition(autoLink: 60, review: 70));
        Assert.Throws<ArgumentException>(() => Definition(allowed: ["Name"], blocked: ["name"]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition(allowed: ["Unknown"])), Actor, default));
    }

    [Fact]
    public async Task Production_topology_requires_exactly_one_OrchestraMSP_endpoint()
    {
        var fixture = Fixture.Create();

        var vendorToVendor = Definition(sourceVendor: "NetSuite", sourceConnectionId: "netsuite");
        var orchestraToOrchestra = Definition(
            targetVendor: "OrchestraMSP",
            targetConnectionId: "orchestra",
            targetEntityType: "Client");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(vendorToVendor), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(orchestraToOrchestra), Actor, default));
    }

    [Fact]
    public async Task Validation_rejects_unknown_vendor_entity_and_unsupported_actions_or_fields()
    {
        var fixture = Fixture.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition(targetVendor: "Unknown")), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition(targetEntityType: "Site")), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition(createMissing: true)), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition(targetCustomFieldName: "Unsupported")), Actor, default));
    }

    [Fact]
    public async Task Scheduled_safe_policy_requires_only_adapter_declared_safe_actions_and_fields()
    {
        var fixture = Fixture.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(
            "tenant",
            Request(Definition(allowed: ["Phone"], scheduledSafe: true)),
            Actor,
            default));

        var accepted = await fixture.Service.CreateAsync(
            "tenant",
            Request(Definition(allowed: ["Name"], scheduledSafe: true)),
            Actor,
            default);
        Assert.True(accepted.Definition.ScheduledApplySafeSubset);
    }

    [Fact]
    public async Task Validation_requires_current_enabled_matching_tenant_connection_generations()
    {
        var fixture = Fixture.Create();
        fixture.Connections.Add(Connection("other", "orchestra", "OrchestraMSP", 1, true));

        await Assert.ThrowsAsync<ConnectionNotFoundException>(() => fixture.Service.CreateAsync(
            "other", Request(Definition()), Actor, default));

        fixture.Connections.Replace(Connection("tenant", "halo", "HaloPSA", 2, false));
        await Assert.ThrowsAsync<ConnectionDisabledException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition()), Actor, default));
    }

    [Fact]
    public async Task Rotation_during_capability_validation_rejects_the_stale_policy()
    {
        var fixture = Fixture.Create();
        fixture.Runtime.AfterCapabilities = () =>
            fixture.Connections.Replace(Connection("tenant", "halo", "HaloPSA", 2, true));

        await Assert.ThrowsAsync<StaleConnectionGenerationException>(() => fixture.Service.CreateAsync(
            "tenant", Request(Definition()), Actor, default));

        Assert.Equal(0, fixture.Policies.RowCount);
        Assert.True(fixture.Runtime.AllAdaptersDisposed);
    }

    [Fact]
    public async Task Capability_validation_leases_are_generation_pinned_and_disposed()
    {
        var fixture = Fixture.Create();

        await fixture.Service.CreateAsync("tenant", Request(Definition()), Actor, default);

        Assert.Equal([1L, 1L], fixture.Runtime.AcquiredGenerations.Order().ToArray());
        Assert.True(fixture.Runtime.AllAdaptersDisposed);
    }

    [Fact]
    public async Task Latest_listing_is_tenant_scoped_and_returns_only_latest_rows()
    {
        var fixture = Fixture.Create();
        var first = await fixture.Service.CreateAsync("tenant", Request(Definition()), Actor, default);
        await fixture.Service.CreateNextVersionAsync(
            "tenant", first.PolicyId, 1, Definition(allowed: ["Name"]), false, Actor, default);

        var latest = await fixture.Service.ListLatestAsync("tenant", null, null, default);
        var otherTenant = await fixture.Service.ListLatestAsync("other", null, null, default);

        Assert.Single(latest);
        Assert.Equal(2, latest[0].Version);
        Assert.Empty(otherTenant);
        await Assert.ThrowsAsync<PolicyNotFoundException>(
            () => fixture.Service.GetVersionAsync("other", first.PolicyId, 1, default));
    }

    private static SyncPolicyRequest Request(EntitySyncPolicyDefinition definition) =>
        new("Client synchronization", "orchestra-halo", definition, true);

    private static EntitySyncPolicyDefinition Definition(
        string sourceVendor = "OrchestraMSP",
        string sourceConnectionId = "orchestra",
        string sourceEntityType = "Client",
        string targetVendor = "HaloPSA",
        string targetConnectionId = "halo",
        string targetEntityType = "Client",
        bool createMissing = false,
        int autoLink = 90,
        int review = 70,
        string? targetCustomFieldName = "ExternalId",
        IEnumerable<string>? allowed = null,
        IEnumerable<string>? blocked = null,
        bool scheduledSafe = false) =>
        new(
            sourceVendor,
            sourceConnectionId,
            sourceEntityType,
            targetVendor,
            targetConnectionId,
            targetEntityType,
            false,
            createMissing,
            autoLink,
            review,
            "Id",
            targetCustomFieldName,
            EntitySyncUpdatePolicy.Standard,
            allowed ?? ["Name"],
            blocked ?? [],
            scheduledSafe);

    private static EntitySyncConnectionDefinition Connection(
        string tenant,
        string id,
        string vendor,
        long generation,
        bool enabled) =>
        new(
            tenant,
            id,
            vendor,
            id,
            generation,
            enabled,
            new EntitySyncJsonValue("{}"),
            "ciphertext",
            Instant,
            Actor,
            Instant,
            Actor);

    private sealed class Fixture
    {
        private Fixture(
            DefinitionRepository connections,
            PolicyRepository policies,
            CapabilityRuntimeFactory runtime,
            SyncPolicyService service)
        {
            Connections = connections;
            Policies = policies;
            Runtime = runtime;
            Service = service;
        }

        public DefinitionRepository Connections { get; }
        public PolicyRepository Policies { get; }
        public CapabilityRuntimeFactory Runtime { get; }
        public SyncPolicyService Service { get; }

        public static Fixture Create()
        {
            var connections = new DefinitionRepository();
            connections.Add(Connection("tenant", "orchestra", "OrchestraMSP", 1, true));
            connections.Add(Connection("tenant", "halo", "HaloPSA", 1, true));
            connections.Add(Connection("tenant", "netsuite", "NetSuite", 1, true));
            var policies = new PolicyRepository(connections);
            var runtime = new CapabilityRuntimeFactory(connections);
            var service = new SyncPolicyService(
                policies,
                connections,
                runtime,
                new FixedTimeProvider(Instant));
            return new Fixture(connections, policies, runtime, service);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DefinitionRepository : IConnectionDefinitionRepository
    {
        private readonly object gate = new();
        private readonly Dictionary<string, EntitySyncConnectionDefinition> values = new(StringComparer.OrdinalIgnoreCase);

        public void Add(EntitySyncConnectionDefinition definition)
        {
            lock (gate) values.Add(Key(definition.TenantId, definition.ConnectionId), definition);
        }

        public void Replace(EntitySyncConnectionDefinition definition)
        {
            lock (gate) values[Key(definition.TenantId, definition.ConnectionId)] = definition;
        }

        public Task InsertAsync(string tenantId, EntitySyncConnectionDefinition definition, CancellationToken cancellationToken)
        {
            Add(definition);
            return Task.CompletedTask;
        }

        public Task<EntitySyncConnectionDefinition?> GetAsync(string tenantId, string connectionId, CancellationToken cancellationToken)
        {
            lock (gate) return Task.FromResult(values.GetValueOrDefault(Key(tenantId, connectionId)));
        }

        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
            string tenantId,
            string? vendor,
            bool? enabled,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                IReadOnlyList<EntitySyncConnectionDefinition> result = values.Values
                    .Where(value => value.TenantId == tenantId)
                    .Where(value => vendor is null || value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                    .Where(value => enabled is null || value.Enabled == enabled)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<bool> TryReplaceAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            EntitySyncConnectionDefinition nextGeneration,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var key = Key(tenantId, connectionId);
                if (!values.TryGetValue(key, out var current) || current.Generation != expectedGeneration)
                    return Task.FromResult(false);
                values[key] = nextGeneration;
                return Task.FromResult(true);
            }
        }

        public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionDefinitionDeleteResult.Referenced);

        private static string Key(string tenant, string id) => $"{tenant}\n{id}";
    }

    private sealed class PolicyRepository(DefinitionRepository connections) : ISyncPolicyRepository
    {
        private readonly object gate = new();
        private readonly List<EntitySyncPolicy> rows = [];
        public int RowCount { get { lock (gate) return rows.Count; } }

        public Task InsertAsync(string tenantId, EntitySyncPolicy policy, CancellationToken cancellationToken)
        {
            lock (gate) rows.Add(policy);
            return Task.CompletedTask;
        }

        public async Task<bool> TryInsertValidatedAsync(
            string tenantId,
            EntitySyncPolicy policy,
            string sourceConnectionId,
            long sourceGeneration,
            string targetConnectionId,
            long targetGeneration,
            CancellationToken cancellationToken)
        {
            var source = await connections.GetAsync(tenantId, sourceConnectionId, cancellationToken);
            var target = await connections.GetAsync(tenantId, targetConnectionId, cancellationToken);
            if (source?.Generation != sourceGeneration || source.Enabled != true
                || target?.Generation != targetGeneration || target.Enabled != true)
                return false;
            lock (gate)
            {
                if (rows.Any(row => row.TenantId == tenantId
                    && row.PolicyId == policy.PolicyId
                    && row.Version == policy.Version))
                    return false;
                rows.Add(policy);
                return true;
            }
        }

        public Task<EntitySyncPolicy?> GetAsync(string tenantId, Guid policyId, int version, CancellationToken cancellationToken)
        {
            lock (gate)
                return Task.FromResult(rows.SingleOrDefault(row => row.TenantId == tenantId && row.PolicyId == policyId && row.Version == version));
        }

        public Task<EntitySyncPolicy?> GetLatestAsync(string tenantId, Guid policyId, CancellationToken cancellationToken)
        {
            lock (gate)
                return Task.FromResult(rows.Where(row => row.TenantId == tenantId && row.PolicyId == policyId).OrderByDescending(row => row.Version).FirstOrDefault());
        }

        public Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(
            string tenantId,
            string? routeScope,
            bool? enabled,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                IReadOnlyList<EntitySyncPolicy> result = rows
                    .Where(row => row.TenantId == tenantId)
                    .GroupBy(row => row.PolicyId)
                    .Select(group => group.OrderByDescending(row => row.Version).First())
                    .Where(row => routeScope is null || row.RouteScope == routeScope)
                    .Where(row => enabled is null || row.Enabled == enabled)
                    .ToArray();
                return Task.FromResult(result);
            }
        }
    }

    private sealed class CapabilityRuntimeFactory(DefinitionRepository connections) : IConnectionRuntimeFactory
    {
        private readonly List<CapabilityAdapter> adapters = [];
        private Action? afterCapabilities;
        public Action? AfterCapabilities
        {
            get => afterCapabilities;
            set => afterCapabilities = value;
        }
        public List<long> AcquiredGenerations { get; } = [];
        public bool AllAdaptersDisposed => adapters.All(adapter => adapter.Disposed);

        public async Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            var definition = await connections.GetAsync(tenantId, connectionId, cancellationToken)
                ?? throw new ConnectionNotFoundException(tenantId, connectionId);
            if (!definition.Enabled) throw new ConnectionDisabledException(connectionId);
            if (definition.Generation != expectedGeneration)
                throw new StaleConnectionGenerationException(connectionId, expectedGeneration, definition.Generation);
            AcquiredGenerations.Add(expectedGeneration);
            var adapter = new CapabilityAdapter(definition.Vendor, () =>
            {
                var callback = Interlocked.Exchange(ref afterCapabilities, null);
                callback?.Invoke();
            });
            adapters.Add(adapter);
            return new RuntimeLease(definition, adapter);
        }

        public async Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            var matches = await connections.ListAsync(
                tenantId,
                vendor,
                enabled: true,
                cancellationToken);
            var definition = connectionId is null
                ? Assert.Single(matches)
                : matches.Single(value => value.ConnectionId == connectionId);
            return await AcquireAsync(
                tenantId,
                definition.ConnectionId,
                definition.Generation,
                cancellationToken);
        }

        public async Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            var matches = await connections.ListAsync(
                tenantId,
                vendor,
                enabled: true,
                cancellationToken);
            return connectionId is null
                ? Assert.Single(matches)
                : matches.Single(value => value.ConnectionId == connectionId);
        }
    }

    private sealed class RuntimeLease(
        EntitySyncConnectionDefinition definition,
        CapabilityAdapter adapter) : IConnectionRuntimeLease
    {
        public EntitySyncConnectionDefinition Definition { get; } = definition;
        public IEntityAdapter Adapter { get; } = adapter;

        public ValueTask DisposeAsync()
        {
            adapter.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapabilityAdapter(string vendor, Action afterCapabilities) : IEntityAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public bool Disposed { get; private set; }

        public Task<EntityAdapterCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            var capabilities = Vendor.Equals("OrchestraMSP", StringComparison.OrdinalIgnoreCase)
                ? new EntityAdapterCapabilities(Vendor,
                    [new EntityTypeCapabilities(
                        "Client", ["Read", "Create", "Update"], ["Id", "Name", "Phone"], ["Name"])])
                : Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
                    ? new EntityAdapterCapabilities(Vendor,
                        [new EntityTypeCapabilities(
                            "Client", ["Read", "Update"], ["ExternalId", "Name", "Phone"], ["Name"])])
                    : new EntityAdapterCapabilities(Vendor,
                        [new EntityTypeCapabilities("Customer", ["Read"], ["Id", "Name"], [])]);
            afterCapabilities();
            return Task.FromResult(capabilities);
        }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalEntity>>([]);
        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }
}
