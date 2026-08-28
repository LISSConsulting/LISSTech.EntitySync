using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntitySyncApplyCoordinatorTests
{
    [Fact]
    public async Task StartRunsIndependentlyOfRequestCancellationAndIsIdempotent()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var writeStarted = NewSignal();
        var releaseWrite = NewSignal();
        var target = new TestAdapter("HaloPSA", create: async (_, cancellationToken) =>
        {
            writeStarted.TrySetResult(true);
            await releaseWrite.Task.WaitAsync(cancellationToken);
            return SuccessfulWrite();
        });
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 1);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());
        using var requestCancellation = new CancellationTokenSource();

        var first = coordinator.Start("tenant", plan.Id);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        var repeated = coordinator.Start(" tenant ", $" {plan.Id} ");

        Assert.Equal("Applying", first.Status);
        Assert.Equal(first.StartedAt, repeated.StartedAt);
        Assert.Equal(1, target.CreateCalls);

        releaseWrite.TrySetResult(true);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);
        var afterConsumption = coordinator.Start("tenant", plan.Id);

        Assert.Equal("Applied", terminal.Status);
        Assert.Equal(1, terminal.Processed);
        Assert.Equal(1, terminal.Succeeded);
        Assert.Equal(terminal, afterConsumption);
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public async Task TenConcurrentStartsRegisterOneOperationAndPerformOneWrite()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var writeStarted = NewSignal();
        var releaseWrite = NewSignal();
        var target = new TestAdapter("HaloPSA", create: async (_, cancellationToken) =>
        {
            writeStarted.TrySetResult(true);
            await releaseWrite.Task.WaitAsync(cancellationToken);
            return SuccessfulWrite();
        });
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 1);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        var starts = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => coordinator.Start("tenant", plan.Id))));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(starts, snapshot => Assert.Equal(starts[0].StartedAt, snapshot.StartedAt));
        Assert.Equal(1, target.CreateCalls);

        releaseWrite.TrySetResult(true);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);
        Assert.Equal("Applied", terminal.Status);
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public async Task StartReturnsRegisteredOperationWhenPlanReadRacesWithAnotherStart()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new ControllablePlanRepository(new InMemoryEntitySyncPlanRepository());
        var blockedGetEntered = NewSignal();
        var releaseBlockedGet = NewSignal();
        var writeStarted = NewSignal();
        var releaseWrite = NewSignal();
        var target = new TestAdapter("HaloPSA", create: async (_, cancellationToken) =>
        {
            writeStarted.TrySetResult(true);
            await releaseWrite.Task.WaitAsync(cancellationToken);
            return SuccessfulWrite();
        });
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 1);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());
        plans.BlockNextGet(blockedGetEntered, releaseBlockedGet);

        var racingStart = Task.Run(() => coordinator.Start("tenant", plan.Id));
        await blockedGetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var registered = coordinator.Start("tenant", plan.Id);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBlockedGet.TrySetResult(true);
        var raced = await racingStart.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(registered, raced);
        Assert.Equal(1, target.CreateCalls);

        releaseWrite.TrySetResult(true);
        Assert.Equal("Applied", (await WaitForTerminalAsync(coordinator, "tenant", plan.Id)).Status);
    }

    [Fact]
    public async Task StartReturnsBeforeAdapterReturnsItsSynchronouslyBlockedTask()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var synchronousWriteEntered = NewSignal();
        var releaseSynchronousWrite = NewSignal();
        var target = new TestAdapter("HaloPSA", create: (_, _) =>
        {
            synchronousWriteEntered.TrySetResult(true);
            releaseSynchronousWrite.Task.GetAwaiter().GetResult();
            return Task.FromResult(SuccessfulWrite());
        });
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 1);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        var start = Task.Run(() => coordinator.Start("tenant", plan.Id));
        await synchronousWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        EntitySyncApplySnapshot initial;
        try
        {
            initial = await start.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseSynchronousWrite.TrySetResult(true);
        }

        Assert.Equal("Applying", initial.Status);
        Assert.Equal("Applied", (await WaitForTerminalAsync(coordinator, "tenant", plan.Id)).Status);
    }

    [Fact]
    public async Task StartRemovesTerminalOperationWhenItsPlanHasDisappeared()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new ControllablePlanRepository(new InMemoryEntitySyncPlanRepository());
        var target = new TestAdapter("HaloPSA");
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 1);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        coordinator.Start("tenant", plan.Id);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);
        Assert.Equal(terminal, coordinator.Start("tenant", plan.Id));
        plans.HidePlans();

        Assert.Throws<KeyNotFoundException>(() => coordinator.Start("tenant", plan.Id));
        var missing = Assert.Throws<InvalidOperationException>(() => coordinator.Get("tenant", plan.Id));

        Assert.Equal("Apply operation has not been started.", missing.Message);
        Assert.Equal(2, plans.HiddenGetCalls);
    }

    [Fact]
    public async Task ApplicationShutdownPreservesProcessedPrefixAndDoesNotRetry()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var secondWriteStarted = NewSignal();
        var target = new TestAdapter("HaloPSA", create: async (call, cancellationToken) =>
        {
            if (call == 1) return SuccessfulWrite();
            secondWriteStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulWrite();
        });
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 2);
        var lifetime = new TestApplicationLifetime();
        var coordinator = new EntitySyncApplyCoordinator(service, plans, lifetime);

        coordinator.Start("tenant", plan.Id);
        await secondWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();

        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);
        var repeated = coordinator.Start("tenant", plan.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(1, terminal.Processed);
        Assert.Equal(1, terminal.Succeeded);
        Assert.Equal(0, terminal.Failed);
        Assert.NotNull(terminal.CompletedAt);
        Assert.Equal(terminal, repeated);
        Assert.Equal(2, target.CreateCalls);
    }

    [Fact]
    public async Task FailedWritesExposeOnlyBoundedSanitizedFailureSummaries()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        const string rawVendorResponse = "Authorization: Bearer secret-token; vendor body";
        var target = new TestAdapter("HaloPSA", create: (_, _) => Task.FromResult(new EntityWriteResult
        {
            Success = false,
            Message = rawVendorResponse
        }));
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, target, 30);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        coordinator.Start("tenant", plan.Id);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(30, terminal.Processed);
        Assert.Equal(30, terminal.Failed);
        Assert.Equal(25, terminal.Failures.Count);
        var failures = Assert.IsAssignableFrom<IList<EntitySyncApplyFailure>>(terminal.Failures);
        Assert.True(failures.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => failures[0] = failures[0] with { Message = "mutated" });
        Assert.All(terminal.Failures, failure =>
        {
            Assert.Equal("Create", failure.Action);
            Assert.Equal("Target write failed.", failure.Message);
            Assert.DoesNotContain(rawVendorResponse, failure.Message, StringComparison.Ordinal);
        });
        Assert.Equal("One or more items failed.", terminal.Error);
    }

    [Fact]
    public async Task FailureSummaryFieldsAreIndividuallyBounded()
    {
        const int maximumFailureFieldLength = EntitySyncApplyFailure.MaximumFieldLength;
        const string rawVendorResponse = "Authorization: Bearer secret-token; vendor body";
        var longAction = "Update-" + new string('A', maximumFailureFieldLength * 2);
        var longSource = new string('S', maximumFailureFieldLength * 2) + "secret-source-tail";
        var longTarget = new string('T', maximumFailureFieldLength * 2) + "secret-target-tail";
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var sourceRegistration = connections.Register("tenant", "netsuite", new TestAdapter("NetSuite"));
        var targetRegistration = connections.Register(
            "tenant",
            "halo",
            new TestAdapter("HaloPSA", update: (_, _) => Task.FromResult(new EntityWriteResult
            {
                Success = false,
                Message = rawVendorResponse
            })));
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            SourceVendor = "NetSuite",
            SourceEntityType = "Customer",
            TargetVendor = "HaloPSA",
            TargetEntityType = "Client",
            Execution = new EntitySyncPlanExecution
            {
                SourceConnectionId = sourceRegistration.Id,
                SourceConnectionGeneration = sourceRegistration.Generation,
                TargetConnectionId = targetRegistration.Id,
                TargetConnectionGeneration = targetRegistration.Generation
            },
            Items =
            [
                new EntitySyncPlanItem
                {
                    Action = longAction,
                    Source = new ExternalEntity
                    {
                        Vendor = "NetSuite",
                        EntityType = "Customer",
                        Id = "source",
                        Name = longSource
                    },
                    Target = new ExternalEntity
                    {
                        Vendor = "HaloPSA",
                        EntityType = "Client",
                        Id = "target",
                        Name = longTarget
                    }
                }
            ]
        };
        plans.Add(plan);
        var service = CreateService(connections, plans);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        coordinator.Start("tenant", plan.Id);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);

        var failure = Assert.Single(terminal.Failures);
        Assert.All(
            new[] { failure.Action, failure.Source, failure.Target, failure.Message },
            value => Assert.InRange(Assert.IsType<string>(value).Length, 0, maximumFailureFieldLength));
        Assert.Equal(longAction[..maximumFailureFieldLength], failure.Action);
        Assert.Equal(longSource[..maximumFailureFieldLength], failure.Source);
        Assert.Equal(longTarget[..maximumFailureFieldLength], failure.Target);
        Assert.Equal("Target write failed.", failure.Message);
        Assert.DoesNotContain(rawVendorResponse, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-source-tail", failure.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-target-tail", failure.Target, StringComparison.Ordinal);
        Assert.Equal(1, terminal.Failed);
    }

    [Fact]
    public async Task PostRegistrationValidationFailureBecomesSafeObservedTerminalSnapshot()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var originalTarget = new TestAdapter("HaloPSA");
        var (service, plan) = await CreateApprovedPlanAsync(connections, plans, originalTarget, 1);
        connections.Register("tenant", "halo", new TestAdapter("HaloPSA"));
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        coordinator.Start("tenant", plan.Id);
        var terminal = await WaitForTerminalAsync(coordinator, "tenant", plan.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(0, terminal.Processed);
        Assert.Equal("Apply failed.", terminal.Error);
        Assert.DoesNotContain("connection", terminal.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(terminal.Failures);
        Assert.Equal(0, originalTarget.CreateCalls);
        Assert.Equal(terminal, coordinator.Start("tenant", plan.Id));
    }

    [Fact]
    public void GetRejectsAnOperationThatWasNotStartedWithoutReadingThePlan()
    {
        var plans = new ThrowingPlanRepository();
        var service = CreateService(new InMemoryEntityConnectionRepository(), plans);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());

        var error = Assert.Throws<InvalidOperationException>(() => coordinator.Get("tenant", "missing"));

        Assert.Equal("Apply operation has not been started.", error.Message);
        Assert.Equal(0, plans.GetCalls);
    }

    private static async Task<(EntitySyncService Service, EntitySyncPlan Plan)> CreateApprovedPlanAsync(
        InMemoryEntityConnectionRepository connections,
        IEntitySyncPlanRepository plans,
        TestAdapter target,
        int sourceCount)
    {
        var sources = Enumerable.Range(1, sourceCount)
            .Select(index => new ExternalEntity
            {
                Vendor = "NetSuite",
                EntityType = "Customer",
                Id = index.ToString(),
                Name = $"Customer {index}",
                ExternalIds = { ["NetSuiteInternalId"] = index.ToString() }
            })
            .ToArray();
        connections.Register("tenant", "netsuite", new TestAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections, plans);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "NetSuite",
            SourceConnectionId = "netsuite",
            TargetVendor = "HaloPSA",
            TargetConnectionId = "halo",
            CreateMissing = true
        }, CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id, 1, 100);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        return (service, plan);
    }

    private static EntitySyncService CreateService(
        IEntityConnectionRepository connections,
        IEntitySyncPlanRepository plans)
    {
        var mapper = new DefaultEntityMapper();
        return new EntitySyncService(
            new EntitySyncPlanner(
                connections,
                plans,
                new InMemoryEntityExclusionRepository(),
                new WeightedEntityMatcher(),
                mapper,
                new InMemoryEntitySyncChangeStateRepository()),
            connections,
            plans,
            new InMemoryEntityExclusionRepository(),
            mapper);
    }

    private static async Task<EntitySyncApplySnapshot> WaitForTerminalAsync(
        EntitySyncApplyCoordinator coordinator,
        string tenantId,
        string planId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = coordinator.Get(tenantId, planId);
            if (!snapshot.Status.Equals("Applying", StringComparison.Ordinal)) return snapshot;
            await Task.Yield();
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static EntityWriteResult SuccessfulWrite() =>
        new() { Success = true, Id = "created", Message = "Created." };

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication()
        {
            stopping.Cancel();
            stopped.Cancel();
        }
    }

    private sealed class TestAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity>? entities = null,
        Func<int, CancellationToken, Task<EntityWriteResult>>? create = null,
        Func<int, CancellationToken, Task<EntityWriteResult>>? update = null) : IEntityAdapter
    {
        private int createCalls;
        private int updateCalls;

        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int CreateCalls => Volatile.Read(ref createCalls);

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(entities ?? (IReadOnlyList<ExternalEntity>)Array.Empty<ExternalEntity>());
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>(Array.Empty<EntitySyncLookup>());

        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref createCalls);
            return create?.Invoke(call, cancellationToken) ?? Task.FromResult(SuccessfulWrite());
        }

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref updateCalls);
            return update?.Invoke(call, cancellationToken) ?? Task.FromResult(SuccessfulWrite());
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ControllablePlanRepository(IEntitySyncPlanRepository inner) : IEntitySyncPlanRepository
    {
        private TaskCompletionSource<bool>? blockedGetEntered;
        private TaskCompletionSource<bool>? releaseBlockedGet;
        private int blockNextGet;
        private int hidePlans;
        private int hiddenGetCalls;

        public int HiddenGetCalls => Volatile.Read(ref hiddenGetCalls);

        public void BlockNextGet(
            TaskCompletionSource<bool> entered,
            TaskCompletionSource<bool> release)
        {
            blockedGetEntered = entered;
            releaseBlockedGet = release;
            Volatile.Write(ref blockNextGet, 1);
        }

        public void HidePlans() => Volatile.Write(ref hidePlans, 1);

        public void Add(EntitySyncPlan plan) => inner.Add(plan);

        public EntitySyncPlan Get(string tenantId, string planId)
        {
            if (Interlocked.Exchange(ref blockNextGet, 0) == 1)
            {
                blockedGetEntered?.TrySetResult(true);
                releaseBlockedGet?.Task.GetAwaiter().GetResult();
            }
            if (Volatile.Read(ref hidePlans) == 1)
            {
                Interlocked.Increment(ref hiddenGetCalls);
                throw new KeyNotFoundException($"Plan '{planId}' was not found.");
            }
            return inner.Get(tenantId, planId);
        }

        public void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count) =>
            inner.RecordInspection(tenantId, planId, digest, startIndex, count);

        public bool TryApprove(string tenantId, string planId, string digest) =>
            inner.TryApprove(tenantId, planId, digest);

        public bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus) =>
            inner.TryTransition(tenantId, planId, expectedStatus, newStatus);
    }

    private sealed class ThrowingPlanRepository : IEntitySyncPlanRepository
    {
        public int GetCalls { get; private set; }

        public void Add(EntitySyncPlan plan) => throw new NotSupportedException();

        public EntitySyncPlan Get(string tenantId, string planId)
        {
            GetCalls++;
            throw new InvalidOperationException("Plan details must not escape.");
        }

        public void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count) =>
            throw new NotSupportedException();

        public bool TryApprove(string tenantId, string planId, string digest) => throw new NotSupportedException();

        public bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus) =>
            throw new NotSupportedException();
    }
}
