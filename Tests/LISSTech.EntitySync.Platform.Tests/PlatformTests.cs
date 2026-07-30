using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void ConnectionsArePartitionedByTenantAndConnectionId()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        repository.Register("tenant-a", "primary", new FakeAdapter("HaloPSA"));
        repository.Register("tenant-b", "primary", new FakeAdapter("HaloPSA"));
        repository.Register("tenant-a", "secondary", new FakeAdapter("HaloPSA"));

        Assert.Equal(2, repository.List("tenant-a").Count);
        Assert.Single(repository.List("tenant-b"));
        Assert.Equal("secondary", repository.Resolve("tenant-a", "HaloPSA", "secondary").Id);
        Assert.Throws<InvalidOperationException>(() => repository.Resolve("tenant-a", "HaloPSA"));
    }

    [Fact]
    public void ReplacingConnectionIncrementsGenerationAndDisposesOldAdapter()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var oldAdapter = new FakeAdapter("HaloPSA");
        var first = repository.Register("tenant", "halo", oldAdapter);
        var second = repository.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.True(oldAdapter.Disposed);
    }

    [Fact]
    public void ReplacingLeasedConnectionDefersDisposalUntilLeaseEnds()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var oldAdapter = new FakeAdapter("HaloPSA");
        var first = repository.Register("tenant", "halo", oldAdapter);
        using var lease = repository.Acquire("tenant", "HaloPSA", "halo", first.Generation);

        repository.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        Assert.Same(oldAdapter, lease.Connection.Adapter);
        Assert.False(oldAdapter.Disposed);
        lease.Dispose();
        Assert.True(oldAdapter.Disposed);
    }

    [Fact]
    public async Task ApprovedPlanIsAppliedOnlyOnce()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var source = new FakeAdapter("NetSuite", [Source("1", "Acme")]);
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "netsuite", source);
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections);

        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, target.CreateCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public async Task ApplyRejectsConnectionReplacedAfterPlanning()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyKeepsUsingPinnedConnectionWhenItIsReplacedDuringWrite()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTarget = new FakeAdapter("HaloPSA", beforeCreate: async () =>
        {
            writeStarted.SetResult();
            await continueWrite.Task;
        });
        var newTarget = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", oldTarget);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        var applyTask = service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);
        await writeStarted.Task;
        connections.Register("tenant", "halo", newTarget);

        Assert.False(oldTarget.Disposed);
        continueWrite.SetResult();
        var result = await applyTask;
        Assert.True(result.Success);
        Assert.Equal(1, oldTarget.CreateCalls);
        Assert.Equal(0, newTarget.CreateCalls);
        Assert.True(oldTarget.Disposed);
    }

    [Fact]
    public async Task CancelledApplyMovesPlanToFailedTerminalState()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA", beforeCreate: () => Task.FromException(new OperationCanceledException())));
        var service = CreateService(connections, plans);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));

        Assert.Equal(EntitySyncPlanStatuses.Failed, plans.Get("tenant", plan.Id).Status);
    }

    [Fact]
    public async Task PlanInspectionIsCompleteAndPaginated()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 60).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);

        var first = service.GetPlan("tenant", plan.Id, 1, 25);
        var last = service.GetPlan("tenant", plan.Id, 3, 25);

        Assert.Equal(60, first.TotalItems);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(10, last.Items.Count);
        Assert.Equal(first.Digest, last.Digest);
    }

    [Theory]
    [InlineData("NCentral")]
    [InlineData("Bill.com")]
    public async Task ApplicationPlannerRejectsFlowsThatRequireUnavailableSourceWriteBack(string targetVendor)
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var service = CreateService(connections);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            TargetVendor = targetVendor
        }, CancellationToken.None));

        Assert.Contains("source integration-link writeback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovalRequiresInspectionOfEveryPlanItem()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 60).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);

        var first = service.GetPlan("tenant", plan.Id, 1, 25);
        Assert.Throws<InvalidOperationException>(() => service.ApprovePlan("tenant", plan.Id, first.Digest));
        service.GetPlan("tenant", plan.Id, 2, 25);
        service.GetPlan("tenant", plan.Id, 3, 25);

        Assert.Equal(first.Digest, service.ApprovePlan("tenant", plan.Id, first.Digest));
    }

    [Fact]
    public void PlanRepositoryReturnsSnapshotsInsteadOfStoredMutableInstances()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            Items = [new EntitySyncPlanItem { Action = "Create", Source = Source("1", "Acme") }]
        };
        repository.Add(plan);

        plan.Items.Clear();
        var firstRead = repository.Get("tenant", plan.Id);
        firstRead.Items[0].Source.Name = "Changed";
        firstRead.Status = EntitySyncPlanStatuses.Approved;
        var secondRead = repository.Get("tenant", plan.Id);

        Assert.Single(secondRead.Items);
        Assert.Equal("Acme", secondRead.Items[0].Source.Name);
        Assert.Equal(EntitySyncPlanStatuses.Draft, secondRead.Status);
    }

    [Fact]
    public void ExpiredPlansAreEvicted()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan { TenantId = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        repository.Add(plan);

        Assert.Throws<InvalidOperationException>(() => repository.Get("tenant", plan.Id));
        Assert.Throws<KeyNotFoundException>(() => repository.Get("tenant", plan.Id));
    }

    [Fact]
    public void ApplyingPlanCanReachTerminalStateAfterExpiration()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan { TenantId = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        repository.Add(plan);
        Assert.True(repository.TryTransition("tenant", plan.Id, EntitySyncPlanStatuses.Draft, EntitySyncPlanStatuses.Applying));
        Thread.Sleep(200);

        Assert.True(repository.TryTransition("tenant", plan.Id, EntitySyncPlanStatuses.Applying, EntitySyncPlanStatuses.Applied));
        Assert.Throws<InvalidOperationException>(() => repository.Get("tenant", plan.Id));
    }

    [Fact]
    public void PlanSnapshotsPreserveCaseInsensitiveEntityFields()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var source = Source("1", "Acme");
        source.ExternalIds.Clear();
        source.ExternalIds["mixedCaseId"] = "42";
        source.CustomFields["mixedCaseField"] = "value";
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            Items = [new EntitySyncPlanItem { Action = "Create", Source = source }]
        };
        repository.Add(plan);

        var snapshot = repository.Get("tenant", plan.Id).Items[0].Source;

        Assert.Equal("42", snapshot.GetExternalId("MIXEDCASEID"));
        Assert.Equal("value", snapshot.GetCustomField("MIXEDCASEFIELD"));
    }

    [Fact]
    public async Task PlanningRejectsUnboundedEntitySets()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 5001).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(Request(), CancellationToken.None));

        Assert.Contains("limited to 5000", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceAdaptersRuntimeOrPowerShell()
    {
        var references = typeof(EntitySyncService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("LISSTech.EntitySync.Adapters", references);
        Assert.DoesNotContain("LISSTech.EntitySync.Runtime", references);
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void CoreAssemblyHasNoFirstPartyOrPowerShellDependencies()
    {
        var references = typeof(EntitySyncPlan).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, reference => reference.StartsWith("LISSTech.EntitySync.", StringComparison.Ordinal));
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void ReviewedPlansRejectUnapprovedExecutableItems()
    {
        var plan = new EntitySyncPlan
        {
            ReviewRequired = true,
            Items =
            [
                new EntitySyncPlanItem
                {
                    Action = "Create",
                    Status = "Planned",
                    Source = Source("1", "Acme")
                }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => ReviewedPlanPolicy.EnsureApproved(plan));
        plan.Items[0].Status = "Accepted";
        ReviewedPlanPolicy.EnsureApproved(plan);
    }

    [Fact]
    public void ImportedExecutableStatusesMustBeReviewedAgain()
    {
        var plan = new EntitySyncPlan
        {
            Items =
            [
                new EntitySyncPlanItem
                {
                    Action = "Create",
                    Status = "Accepted",
                    Source = Source("1", "Acme")
                }
            ]
        };

        ReviewedPlanPolicy.PrepareForReview(plan);

        Assert.True(plan.ReviewRequired);
        Assert.Equal("Planned", plan.Items[0].Status);
        Assert.Throws<InvalidOperationException>(() => ReviewedPlanPolicy.EnsureApproved(plan));
    }

    [Fact]
    public void McpConnectionToolDoesNotExposeEndpointsOrSecrets()
    {
        var parameters = typeof(ConnectionTools).GetMethod(nameof(ConnectionTools.ConnectVendor))!
            .GetParameters()
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(parameters, name => name.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameters, name => name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameters, name => name.Contains("token", StringComparison.OrdinalIgnoreCase) && !name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void McpAssemblyDoesNotReferencePowerShellHost()
    {
        var references = typeof(ConnectionTools).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("LISSTech.EntitySync", references);
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void McpExposesInspectApproveAndApplyWorkflow()
    {
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.GetSyncPlan)));
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.ApproveSyncPlan)));
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.ApplySyncPlan)));
    }

    private static EntitySyncService CreateService(IEntityConnectionRepository connections, IEntitySyncPlanRepository? plans = null)
    {
        plans ??= new InMemoryEntitySyncPlanRepository();
        return new EntitySyncService(new EntitySyncPlanner(connections, plans, new WeightedEntityMatcher()), connections, plans, new DefaultEntityMapper());
    }

    private static CreateEntitySyncPlanRequest Request() => new()
    {
        TenantId = "tenant",
        SourceVendor = "NetSuite",
        SourceConnectionId = "netsuite",
        TargetVendor = "HaloPSA",
        TargetConnectionId = "halo",
        CreateMissing = true
    };

    private static ExternalEntity Source(string id, string name) => new()
    {
        Vendor = "NetSuite",
        EntityType = "Customer",
        Id = id,
        Name = name,
        ExternalIds = { ["NetSuiteInternalId"] = id }
    };

    private sealed class FakeAdapter(string vendor, IReadOnlyList<ExternalEntity>? entities = null, Func<Task>? beforeCreate = null) : IEntityAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int CreateCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(entities ?? (IReadOnlyList<ExternalEntity>)Array.Empty<ExternalEntity>());
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>(Array.Empty<EntitySyncLookup>());

        public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCreate != null) await beforeCreate();
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            return new EntityWriteResult { Success = true, Id = CreateCalls.ToString(), Message = "Created." };
        }

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult { Success = true, Id = request.Id, Message = "Updated." });

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }
}
