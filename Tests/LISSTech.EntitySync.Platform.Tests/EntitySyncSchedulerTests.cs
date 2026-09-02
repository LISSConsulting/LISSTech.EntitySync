using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using LISSTech.EntitySync.Scheduler;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntitySyncSchedulerTests
{
    [Fact]
    public async Task SuccessfulBaselineThenIdenticalRunWritesOnlyOnce()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);

        var first = await fixture.Run.RunAsync(default);
        var second = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", first.State);
        Assert.Equal(1, first.Total);
        Assert.Equal(1, first.Changed);
        Assert.Equal(1, first.Succeeded);
        Assert.Equal("Applied", second.State);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(1, second.ApplySkipped);
        Assert.Equal(1, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task DefaultScheduleRunsNetSuiteThroughHaloToEveryLeafAndThenSkipsIdenticalState()
    {
        using var fixture = SchedulerFixture.FullChainLinkedSources(1);

        var first = await fixture.Run.RunAsync(default);
        var second = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", first.State);
        Assert.Equal(4, first.Total);
        Assert.Equal(4, first.Changed);
        Assert.Equal(4, first.Succeeded);
        Assert.Equal(
            ["HaloPSA", "NCentral", "Bill.com", "HaloPSA", "Sophos Central"],
            fixture.Factory.UpdatedVendors);
        Assert.Equal("Applied", second.State);
        Assert.Equal(4, second.Total);
        Assert.Equal(4, second.Unchanged);
        Assert.Equal(4, second.ApplySkipped);
        Assert.Equal(5, fixture.Factory.UpdateCalls);
        Assert.Equal(10, fixture.Factory.CreateCalls);
        Assert.All(fixture.Factory.Adapters, adapter => Assert.Equal(1, adapter.TestCalls));
        Assert.Contains(
            fixture.Factory.Queries,
            call => call.Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
                && call.Query.RequiredCustomFieldName == EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName);
        Assert.Contains(
            fixture.Factory.Queries,
            call => call.Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
                && call.Query.RequiredCustomFieldName == EntitySyncIntegrationContracts.SophosCentralHaloTenantCustomFieldName);
    }


    [Fact]
    public async Task OneMappedSourceChangeUpdatesOnlyThatSource()
    {
        using var fixture = SchedulerFixture.LinkedSources(2);
        await fixture.Run.RunAsync(default);
        fixture.Sources[1].Email = "changed@example.com";

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", result.State);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.ApplySkipped);
        Assert.Equal(3, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task InactiveSourceIsIncludedInScheduledPlan()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Sources[0].IsActive = false;

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", result.State);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, fixture.Factory.UpdateCalls);
        Assert.True(fixture.Factory.SourceQueries.Single().IncludeInactive);
    }

    [Fact]
    public async Task HeldRouteLockSkipsBeforeVendorConnections()
    {
        using var fixture = SchedulerFixture.LinkedSources(1, lockAvailable: false);

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("SkippedOverlap", result.State);
        Assert.Equal(0, fixture.Factory.CreateCalls);
        Assert.Equal($"{EntitySyncSchedulerOptions.TenantId}|{SchedulerFixture.Scope}", fixture.RunLock.RouteKeys.Single());
    }

    [Fact]
    public async Task EveryRunCreatesFreshAdaptersAndRepositoryDisposesReplacedGenerations()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);

        await fixture.Run.RunAsync(default);
        var firstGeneration = fixture.Factory.Adapters.ToArray();
        await fixture.Run.RunAsync(default);
        var secondGeneration = fixture.Factory.Adapters.Skip(2).ToArray();

        Assert.Equal(4, fixture.Factory.CreateCalls);
        Assert.All(firstGeneration, adapter => Assert.Equal(1, adapter.DisposeCalls));
        Assert.All(secondGeneration, adapter => Assert.Equal(0, adapter.DisposeCalls));
    }

    [Fact]
    public async Task AdapterThatCannotTransferToRepositoryIsDisposedByRun()
    {
        var sources = new[]
        {
            new ExternalEntity
            {
                Vendor = "NetSuite",
                EntityType = "Customer",
                Id = "1",
                Name = "Source",
                ExternalIds = { ["NetSuiteInternalId"] = "1" }
            }
        };
        var targets = new[]
        {
            new ExternalEntity
            {
                Vendor = "HaloPSA",
                EntityType = "Client",
                Id = "target-1",
                Name = "Target",
                CustomFields = { ["CFNetSuiteCustomerID"] = "1" }
            }
        };
        using var connections = new ThrowingRegisterConnectionRepository("HaloPSA");
        var plans = new RecordingPlanRepository(new InMemoryEntitySyncPlanRepository());
        var states = new InMemoryEntitySyncChangeStateRepository();
        var exclusions = new InMemoryEntityExclusionRepository();
        var mapper = new DefaultEntityMapper();
        var factory = new RecordingAdapterFactory(sources, targets);
        var service = new EntitySyncService(
            new EntitySyncPlanner(
                connections,
                plans,
                exclusions,
                new WeightedEntityMatcher(),
                mapper,
                states),
            connections,
            plans,
            exclusions,
            mapper,
            states,
            TimeProvider.System);
        var run = new EntitySyncScheduledRun(
            new EntitySyncSchedulerOptions([EntitySyncSchedulerOptions.NetSuiteToHalo]),
            new FakeRunLock(true),
            factory,
            connections,
            plans,
            service,
            new EntitySyncSchedulerStatus(),
            TimeProvider.System);

        var result = await run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal([0, 1], factory.Adapters.Select(adapter => adapter.DisposeCalls).ToArray());
    }

    [Fact]
    public async Task FailedVendorConnectionReturnsSafeFailureWithoutPlanningOrWriting()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Factory.FailedConnectionVendor = "HaloPSA";

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal("Vendor connection setup or validation failed.", result.Error);
        Assert.Null(result.PlanId);
        Assert.Empty(fixture.Plans.Inspections);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task BothFreshConnectionsAreTestedWhenSourceConnectionFails()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Factory.FailedConnectionVendor = "NetSuite";

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal([1, 1], fixture.Factory.Adapters.Select(adapter => adapter.TestCalls).ToArray());
        Assert.Empty(fixture.Plans.Inspections);
    }

    [Fact]
    public async Task TargetConnectionIsStillTestedWhenSourceProbeThrows()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Factory.ThrowingConnectionVendor = "NetSuite";

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal([1, 1], fixture.Factory.Adapters.Select(adapter => adapter.TestCalls).ToArray());
        Assert.Empty(fixture.Plans.Inspections);
    }

    [Fact]
    public async Task NameOnlyCandidateIsPolicySkippedAndNeverWritten()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Targets[0].CustomFields.Clear();
        fixture.Targets[0].Name = fixture.Sources[0].Name;

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", result.State);
        Assert.Equal(0, result.Changed);
        Assert.Equal(1, result.PolicySkipped);
        Assert.Equal(1, result.ApplySkipped);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("hash")]
    [InlineData("version")]
    public async Task WritableItemsRequireLinkedMatchAndExactHashMetadata(string invalidField)
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Plans.MutateGet = (_, plan) =>
        {
            var item = plan.Items[0];
            if (invalidField == "match") item.MatchType = "HighConfidence";
            if (invalidField == "hash") item.DesiredStateHash = null;
            if (invalidField == "version") item.DesiredStateHashVersion = EntityWriteRequestDigest.SchemaVersion + 1;
            return plan;
        };

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal("Synchronization plan validation failed.", result.Error);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task ProhibitedWritableActionFailsClosedBeforeApprovalAndApply()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Plans.MutateGet = (_, plan) =>
        {
            plan.Items[0].Action = "Create";
            return plan;
        };

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal("Synchronization plan validation failed.", result.Error);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
        Assert.Equal(EntitySyncPlanStatuses.Draft, fixture.Plans.Inner.Get(EntitySyncSchedulerOptions.TenantId, result.PlanId!).Status);
    }

    [Fact]
    public async Task CreateMissingPlanMetadataFailsClosedBeforeApply()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Plans.MutateGet = (_, plan) =>
        {
            plan.Execution.MatchOptions.CreateMissing = true;
            return plan;
        };

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal("Synchronization plan validation failed.", result.Error);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task ScheduledRunInspectsEveryHundredItemPageBeforeApproval()
    {
        using var fixture = SchedulerFixture.LinkedSources(101);

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Applied", result.State);
        Assert.Equal([(0, 100), (100, 1)], fixture.Plans.Inspections);
        Assert.Equal(101, result.Succeeded);
    }

    [Fact]
    public async Task DigestChangeBetweenInspectionPagesFailsClosed()
    {
        using var fixture = SchedulerFixture.LinkedSources(101);
        fixture.Plans.MutateGet = (call, plan) =>
        {
            if (call == 2) plan.CreatedAt = plan.CreatedAt.AddTicks(1);
            return plan;
        };

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.Equal("Synchronization plan validation failed.", result.Error);
        Assert.Equal([(0, 100), (100, 1)], fixture.Plans.Inspections);
        Assert.Equal(0, fixture.Factory.UpdateCalls);
    }

    [Fact]
    public async Task CancellationPreservesCompletedCheckpointPublishesFailureAndPropagates()
    {
        using var fixture = SchedulerFixture.LinkedSources(2);
        using var cancellation = new CancellationTokenSource();
        fixture.Factory.UpdateBehavior = (call, token) =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new EntityWriteResult
            {
                Vendor = "HaloPSA",
                EntityType = "Client",
                Id = call.ToString(),
                Action = "Update",
                Success = true,
                Message = "updated"
            });
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run.RunAsync(cancellation.Token));

        var states = await fixture.ChangeStates.GetBySourceIdsAsync(
            fixture.Route,
            fixture.Sources.Select(source => source.Id).ToArray(),
            default);
        Assert.Equal("Failed", fixture.Status.Snapshot.State);
        Assert.Equal("Scheduled synchronization was cancelled.", fixture.Status.Snapshot.Error);
        Assert.Equal(1, fixture.Status.Snapshot.Succeeded);
        Assert.True(states.ContainsKey(fixture.Sources[0].Id));
        Assert.False(states.ContainsKey(fixture.Sources[1].Id));
    }

    [Fact]
    public async Task CancellationPropagatesWhenAdvisoryLeaseCleanupFails()
    {
        using var fixture = SchedulerFixture.LinkedSources(1, throwOnLockDispose: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Factory.UpdateBehavior = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable.");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Run.RunAsync(cancellation.Token));

        Assert.Equal("Failed", fixture.Status.Snapshot.State);
        Assert.Equal("Scheduled synchronization was cancelled.", fixture.Status.Snapshot.Error);
    }

    [Fact]
    public async Task StatusAndLogsNeverExposeRawFailureDetails()
    {
        using var fixture = SchedulerFixture.LinkedSources(1);
        fixture.Factory.CreationException = new InvalidOperationException(
            "Acme Incorporated token=top-secret mapped-payload customer-name");

        var result = await fixture.Run.RunAsync(default);

        Assert.Equal("Failed", result.State);
        Assert.NotNull(result.Error);
        Assert.InRange(result.Error!.Length, 1, 512);
        Assert.DoesNotContain("Acme", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", result.Error, StringComparison.OrdinalIgnoreCase);
        var log = Assert.Single(fixture.Logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishedStatusIsAnImmutableBoundedSnapshot()
    {
        var store = new EntitySyncSchedulerStatus();
        var original = store.Snapshot;

        store.Publish(original with { State = "Failed", Error = new string('x', 600) });

        Assert.Equal("Waiting", original.State);
        Assert.Null(original.Error);
        Assert.Equal("Failed", store.Snapshot.State);
        Assert.Equal(512, store.Snapshot.Error!.Length);
        Assert.NotSame(original, store.Snapshot);
    }

    [Fact]
    public async Task WorkerRunsImmediatelyThenWaitsTwelveHoursFromCompletion()
    {
        var startedAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var status = new EntitySyncSchedulerStatus();
        var runCalls = 0;
        var run = new DelegateScheduledRun(token =>
        {
            token.ThrowIfCancellationRequested();
            runCalls++;
            time.Advance(TimeSpan.FromHours(1));
            var completedAt = time.GetUtcNow();
            var terminal = status.Snapshot with
            {
                State = "Applied",
                LastStartedAt = startedAt,
                LastCompletedAt = completedAt
            };
            status.Publish(terminal);
            return Task.FromResult(terminal);
        });
        using var worker = new EntitySyncSchedulerWorker(run, status, time);

        await worker.StartAsync(default);
        await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, runCalls);
        Assert.Equal(TimeSpan.FromHours(12), time.LastDueTime);
        Assert.Equal(startedAt.AddHours(13), status.Snapshot.NextRunAt);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task DisabledAutomaticRunsWaitForAuthenticatedManualQueue()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var status = new EntitySyncSchedulerStatus();
        var runCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCalls = 0;
        var run = new DelegateScheduledRun(token =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref runCalls);
            runCompleted.TrySetResult();
            return Task.FromResult(status.Snapshot);
        });
        var options = new EntitySyncSchedulerOptions(
            EntitySyncSchedulerOptions.FullChainRoutes,
            automaticRunsEnabled: false);
        using var worker = new EntitySyncSchedulerWorker(run, status, time, options);

        await worker.StartAsync(default);

        Assert.Equal(0, runCalls);
        Assert.Null(status.Snapshot.NextRunAt);
        Assert.True(worker.TryRequestRun());
        await runCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runCalls);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task FailedRunDoesNotExitWorkerOrRetryBeforeNormalInterval()
    {
        var startedAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var status = new EntitySyncSchedulerStatus();
        var runCalls = 0;
        var run = new DelegateScheduledRun(token =>
        {
            token.ThrowIfCancellationRequested();
            runCalls++;
            var terminal = status.Snapshot with
            {
                State = "Failed",
                LastStartedAt = time.GetUtcNow(),
                LastCompletedAt = time.GetUtcNow(),
                Error = "Vendor connection setup or validation failed."
            };
            status.Publish(terminal);
            return Task.FromResult(terminal);
        });
        using var worker = new EntitySyncSchedulerWorker(run, status, time);

        await worker.StartAsync(default);
        await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runCalls);

        time.Advance(EntitySyncSchedulerOptions.Interval);
        time.FireLastTimer();
        await time.SecondTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runCalls);
        Assert.Equal("Failed", status.Snapshot.State);
        Assert.Equal(TimeSpan.FromHours(12), time.LastDueTime);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task OnDemandRunPreservesFutureScheduledDeadlineAndRejectsDuplicateQueue()
    {
        var startedAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var status = new EntitySyncSchedulerStatus();
        var secondRunCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCalls = 0;
        var run = new DelegateScheduledRun(token =>
        {
            token.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref runCalls);
            var terminal = status.Snapshot with
            {
                State = "Applied",
                LastStartedAt = time.GetUtcNow(),
                LastCompletedAt = time.GetUtcNow()
            };
            status.Publish(terminal);
            if (call == 2) secondRunCompleted.TrySetResult();
            return Task.FromResult(terminal);
        });
        using var worker = new EntitySyncSchedulerWorker(run, status, time);

        await worker.StartAsync(default);
        await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromHours(1));

        Assert.True(worker.TryRequestRun());
        Assert.False(worker.TryRequestRun());
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await time.SecondTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runCalls);
        Assert.Equal(startedAt.AddHours(12), status.Snapshot.NextRunAt);
        Assert.Equal(TimeSpan.FromHours(11), time.LastDueTime);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task OnDemandRunIsRejectedWhileScheduledRunIsActive()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var status = new EntitySyncSchedulerStatus();
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new DelegateScheduledRun(async token =>
        {
            runStarted.TrySetResult();
            await releaseRun.Task.WaitAsync(token);
            var terminal = status.Snapshot with
            {
                State = "Applied",
                LastStartedAt = time.GetUtcNow(),
                LastCompletedAt = time.GetUtcNow()
            };
            status.Publish(terminal);
            return terminal;
        });
        using var worker = new EntitySyncSchedulerWorker(run, status, time);

        await worker.StartAsync(default);
        await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(worker.TryRequestRun());

        releaseRun.TrySetResult();
        await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
    }

    private sealed class SchedulerFixture : IDisposable
    {
        public const string Scope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private readonly InMemoryEntityConnectionRepository connections = new();
        private bool disposed;

        private SchedulerFixture(int count, bool lockAvailable, bool throwOnLockDispose, bool fullChain = false)
        {
            Sources = Enumerable.Range(1, count).Select(index => Source(index.ToString(), $"Source {index}")).ToList();
            Targets = Enumerable.Range(1, count).Select(index => LinkedTarget($"target-{index}", index.ToString(), $"Target {index}")).ToList();
            IReadOnlyDictionary<string, IReadOnlyList<ExternalEntity>>? entitiesByVendor = null;
            if (fullChain)
            {
                foreach (var (target, index) in Targets.Select((target, index) => (target, index + 1)))
                {
                    target.ExternalIds["NCentralCustomerId"] = $"ncentral-{index}";
                    target.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = $"bill-{index}";
                    target.ExternalIds[EntitySyncIntegrationContracts.SophosCentralTenantExternalIdName] = $"sophos-{index}";
                }
                entitiesByVendor = new Dictionary<string, IReadOnlyList<ExternalEntity>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NetSuite"] = Sources,
                    ["HaloPSA"] = Targets,
                    ["NCentral"] = Enumerable.Range(1, count).Select(index => LeafTarget("NCentral", "Customer", $"ncentral-{index}", "NCentralCustomerId")).ToArray(),
                    ["Bill.com"] = Enumerable.Range(1, count)
                        .Select(index => LeafTarget("Bill.com", "Client", $"bill-{index}", EntitySyncIntegrationContracts.BillComClientExternalIdName))
                        .Append(LeafTarget("Bill.com", "Client", "bill-orphan", EntitySyncIntegrationContracts.BillComClientExternalIdName))
                        .ToArray(),
                    ["Sophos Central"] = Enumerable.Range(1, count).Select(index => LeafTarget("Sophos Central", "Customer", $"sophos-{index}", EntitySyncIntegrationContracts.SophosCentralTenantExternalIdName)).ToArray()
                };
            }
            Factory = new RecordingAdapterFactory(Sources, Targets, entitiesByVendor);
            RunLock = new FakeRunLock(lockAvailable, throwOnLockDispose);
            Plans = new RecordingPlanRepository(new InMemoryEntitySyncPlanRepository());
            ChangeStates = new InMemoryEntitySyncChangeStateRepository();
            var mapper = new DefaultEntityMapper();
            var service = new EntitySyncService(
                new EntitySyncPlanner(
                    connections,
                    Plans,
                    new InMemoryEntityExclusionRepository(),
                    new WeightedEntityMatcher(),
                    mapper,
                    ChangeStates),
                connections,
                Plans,
                new InMemoryEntityExclusionRepository(),
                mapper,
                ChangeStates,
                TimeProvider.System);
            Status = new EntitySyncSchedulerStatus();
            Logger = new RecordingLogger<EntitySyncScheduledRun>();
            Run = new EntitySyncScheduledRun(
                fullChain
                    ? new EntitySyncSchedulerOptions()
                    : new EntitySyncSchedulerOptions([EntitySyncSchedulerOptions.NetSuiteToHalo]),
                RunLock,
                Factory,
                connections,
                Plans,
                service,
                Status,
                TimeProvider.System,
                Logger);
            Route = EntitySyncChangeStateRoute.Create(
                EntitySyncSchedulerOptions.TenantId,
                Scope,
                EntitySyncSchedulerOptions.SourceVendor,
                EntitySyncSchedulerOptions.SourceConnectionId,
                EntitySyncSchedulerOptions.SourceEntityType,
                EntitySyncSchedulerOptions.TargetVendor,
                EntitySyncSchedulerOptions.TargetConnectionId,
                EntitySyncSchedulerOptions.TargetEntityType);
        }

        public List<ExternalEntity> Sources { get; }
        public List<ExternalEntity> Targets { get; }
        public RecordingAdapterFactory Factory { get; }
        public FakeRunLock RunLock { get; }
        public RecordingPlanRepository Plans { get; }
        public InMemoryEntitySyncChangeStateRepository ChangeStates { get; }
        public EntitySyncSchedulerStatus Status { get; }
        public RecordingLogger<EntitySyncScheduledRun> Logger { get; }
        public EntitySyncScheduledRun Run { get; }
        public EntitySyncChangeStateRoute Route { get; }
        public static SchedulerFixture LinkedSources(
            int count,
            bool lockAvailable = true,
            bool throwOnLockDispose = false) =>
            new(count, lockAvailable, throwOnLockDispose);

        public static SchedulerFixture FullChainLinkedSources(int count) =>
            new(count, lockAvailable: true, throwOnLockDispose: false, fullChain: true);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            connections.Dispose();
        }

        private static ExternalEntity Source(string id, string name) => new()
        {
            Vendor = "NetSuite",
            EntityType = "Customer",
            Id = id,
            Name = name,
            Email = $"source-{id}@example.com",
            ExternalIds = { ["NetSuiteInternalId"] = id }
        };

        private static ExternalEntity LinkedTarget(string id, string sourceId, string name) => new()
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = id,
            Name = name,
            CustomFields = { ["CFNetSuiteCustomerID"] = sourceId }
        };

        private static ExternalEntity LeafTarget(
            string vendor,
            string entityType,
            string id,
            string externalIdName) => new()
        {
            Vendor = vendor,
            EntityType = entityType,
            Id = id,
            Name = $"Target {id[(id.LastIndexOf('-') + 1)..]}",
            ExternalIds = { [externalIdName] = id }
        };
    }

    private sealed class RecordingAdapterFactory(
        IReadOnlyList<ExternalEntity> sources,
        IReadOnlyList<ExternalEntity> targets,
        IReadOnlyDictionary<string, IReadOnlyList<ExternalEntity>>? entitiesByVendor = null) : IServerManagedEntityAdapterFactory
    {
        private int updateCalls;

        public int CreateCalls { get; private set; }
        public int UpdateCalls => Volatile.Read(ref updateCalls);
        public string? FailedConnectionVendor { get; set; }
        public string? ThrowingConnectionVendor { get; set; }
        public Exception? CreationException { get; set; }
        public Func<int, CancellationToken, Task<EntityWriteResult>>? UpdateBehavior { get; set; }
        public List<TestAdapter> Adapters { get; } = [];
        public List<string> UpdatedVendors { get; } = [];
        public List<(string Vendor, EntityQuery Query)> Queries { get; } = [];
        public List<EntityQuery> SourceQueries { get; } = [];

        public Task<IEntityAdapter> CreateAsync(
            string vendor,
            IReadOnlyDictionary<string, string>? profileSettings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            if (CreationException is not null) throw CreationException;
            var normalized = EntitySyncVendors.Normalize(vendor);
            var entities = entitiesByVendor?.GetValueOrDefault(normalized)
                ?? (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase) ? sources : targets);
            var adapter = new TestAdapter(
                normalized,
                entities,
                query =>
                {
                    Queries.Add((normalized, query));
                    if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase)) SourceQueries.Add(query);
                },
                token =>
                {
                    var call = Interlocked.Increment(ref updateCalls);
                    UpdatedVendors.Add(normalized);
                    return UpdateBehavior?.Invoke(call, token)
                        ?? Task.FromResult(new EntityWriteResult
                        {
                            Vendor = normalized,
                            EntityType = normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) ? "Client" : "Customer",
                            Action = "Update",
                            Success = true,
                            Message = "updated"
                        });
                },
                () =>
                {
                    if (normalized.Equals(ThrowingConnectionVendor, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Connection probe failed.");
                    return !normalized.Equals(FailedConnectionVendor, StringComparison.OrdinalIgnoreCase);
                });
            Adapters.Add(adapter);
            return Task.FromResult<IEntityAdapter>(adapter);
        }

        public void ValidateConfiguration(IEnumerable<string> vendors)
        {
        }

        public string GetChangeStateScope(
            string sourceVendor,
            string sourceConnectionId,
            string sourceEntityType,
            string targetVendor,
            string targetConnectionId,
            string targetEntityType) => SchedulerFixture.Scope;
    }

    private sealed class TestAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity> entities,
        Action<EntityQuery> recordQuery,
        Func<CancellationToken, Task<EntityWriteResult>> update,
        Func<bool> connectionTest) : IEntityAdapter, IHaloSourceWritebackAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int DisposeCalls { get; private set; }
        public int TestCalls { get; private set; }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordQuery(query);
            var result = query.IncludeInactive
                ? entities
                : entities.Where(entity => entity.IsActive != false).ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Scheduled sync must never create entities.");

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            update(cancellationToken);

        public Task<EntityWriteResult> UpsertNCentralClientLinkAsync(
            string haloClientId,
            string haloClientName,
            string nCentralCustomerId,
            string nCentralCustomerName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult
            {
                Vendor = "HaloPSA",
                EntityType = "NCentralIntegrationLink",
                Id = nCentralCustomerId,
                Action = "ClientLink",
                Success = true
            });

        public Task<EntityWriteResult> UpsertNCentralSiteLinkAsync(
            string haloSiteId,
            string haloSiteName,
            string haloClientName,
            string nCentralSiteId,
            string nCentralSiteName,
            string nCentralCustomerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult
            {
                Vendor = "HaloPSA",
                EntityType = "NCentralIntegrationLink",
                Id = nCentralSiteId,
                Action = "SiteLink",
                Success = true
            });

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
        {
            TestCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(connectionTest());
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeRunLock(bool available, bool throwOnDispose = false) : IEntitySyncSchedulerRunLock
    {
        public List<string> RouteKeys { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireAsync(string routeKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouteKeys.Add(routeKey);
            return Task.FromResult<IAsyncDisposable?>(
                available ? new Lease(throwOnDispose) : null);
        }

        private sealed class Lease(bool throwOnDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() =>
                throwOnDispose
                    ? ValueTask.FromException(new InvalidOperationException("Lock cleanup failed."))
                    : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPlanRepository(IEntitySyncPlanRepository inner) : IEntitySyncPlanRepository
    {
        private int getCalls;

        public IEntitySyncPlanRepository Inner { get; } = inner;
        public Func<int, EntitySyncPlan, EntitySyncPlan>? MutateGet { get; set; }
        public List<(int Start, int Count)> Inspections { get; } = [];

        public void Add(EntitySyncPlan plan) => Inner.Add(plan);

        public EntitySyncPlan Get(string tenantId, string planId)
        {
            var plan = Inner.Get(tenantId, planId);
            var call = Interlocked.Increment(ref getCalls);
            return MutateGet?.Invoke(call, plan) ?? plan;
        }

        public void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count)
        {
            Inspections.Add((startIndex, count));
            Inner.RecordInspection(tenantId, planId, digest, startIndex, count);
        }

        public bool TryApprove(string tenantId, string planId, string digest) =>
            Inner.TryApprove(tenantId, planId, digest);

        public bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus) =>
            Inner.TryTransition(tenantId, planId, expectedStatus, newStatus);
    }

    private sealed class ThrowingRegisterConnectionRepository(string failedVendor)
        : IEntityConnectionRepository, IDisposable
    {
        private readonly InMemoryEntityConnectionRepository inner = new();

        public IEntityConnectionAdmission BeginRegistration(string tenantId, string? connectionId, string vendor) =>
            inner.BeginRegistration(tenantId, connectionId, vendor);

        public EntityConnectionRegistration Register(
            string tenantId,
            string? connectionId,
            IEntityAdapter adapter)
        {
            if (adapter.Vendor.Equals(failedVendor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Registration failed.");
            return inner.Register(tenantId, connectionId, adapter);
        }

        public EntityConnectionRegistration Resolve(string tenantId, string vendor, string? connectionId = null) =>
            inner.Resolve(tenantId, vendor, connectionId);

        public IEntityConnectionLease Acquire(
            string tenantId,
            string vendor,
            string? connectionId = null,
            long? generation = null) =>
            inner.Acquire(tenantId, vendor, connectionId, generation);

        public IReadOnlyList<EntityConnectionRegistration> List(string tenantId) => inner.List(tenantId);
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class DelegateScheduledRun(
        Func<CancellationToken, Task<EntitySyncSchedulerStatusSnapshot>> run) : IEntitySyncScheduledRun
    {
        public Task<EntitySyncSchedulerStatusSnapshot> RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;
        private ManualTimer? lastTimer;
        private int timerCount;

        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan? LastDueTime { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;

        public void FireLastTimer() =>
            (lastTimer ?? throw new InvalidOperationException("No timer has been created.")).Fire();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            LastDueTime = dueTime;
            lastTimer = new ManualTimer(callback, state);
            var count = Interlocked.Increment(ref timerCount);
            if (count == 1) TimerCreated.TrySetResult();
            if (count == 2) SecondTimerCreated.TrySetResult();
            return lastTimer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
            public void Fire()
            {
                if (Volatile.Read(ref disposed) == 0) callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
