using System.Collections.Concurrent;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Hosting;

namespace LISSTech.EntitySync.Mcp;

public sealed record EntitySyncApplyFailure(
    string Action,
    string Source,
    string? Target,
    string Message);

public sealed record EntitySyncApplySnapshot(
    string PlanId,
    string Status,
    int Total,
    int Processed,
    int Succeeded,
    int Failed,
    int Skipped,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<EntitySyncApplyFailure> Failures,
    string? Error);

public sealed class EntitySyncApplyCoordinator
{
    private readonly EntitySyncService service;
    private readonly IEntitySyncPlanRepository plans;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, ApplyOperation> operations = new(StringComparer.Ordinal);

    public EntitySyncApplyCoordinator(
        EntitySyncService service,
        IEntitySyncPlanRepository plans,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider? timeProvider = null)
    {
        this.service = service;
        this.plans = plans;
        this.applicationLifetime = applicationLifetime;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public EntitySyncApplySnapshot Start(string tenantId, string planId)
    {
        var key = Key(tenantId, planId);
        if (operations.TryGetValue(key, out var existing)) return existing.Snapshot;

        var plan = plans.Get(tenantId, planId);
        if (!plan.Status.Equals(EntitySyncPlanStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plan must be approved before apply.");

        var candidate = new ApplyOperation(plan.Id, plan.Items.Count, timeProvider.GetUtcNow());
        var operation = operations.GetOrAdd(key, candidate);
        if (ReferenceEquals(operation, candidate))
            operation.Start(RunAsync(tenantId, plan.Id, operation));
        return operation.Snapshot;
    }

    public EntitySyncApplySnapshot Get(string tenantId, string planId)
    {
        if (operations.TryGetValue(Key(tenantId, planId), out var operation)) return operation.Snapshot;
        throw new InvalidOperationException("Apply operation has not been started.");
    }

    private async Task RunAsync(string tenantId, string planId, ApplyOperation operation)
    {
        try
        {
            var result = await service.ApplyAsync(
                tenantId,
                planId,
                true,
                applicationLifetime.ApplicationStopping,
                operation.ReportProgress).ConfigureAwait(false);
            operation.Complete(
                result.Success ? EntitySyncPlanStatuses.Applied : EntitySyncPlanStatuses.Failed,
                timeProvider.GetUtcNow(),
                result.Success ? null : "One or more items failed.");
        }
        catch (OperationCanceledException)
        {
            operation.Complete(
                EntitySyncPlanStatuses.Failed,
                timeProvider.GetUtcNow(),
                "Apply stopped because the application is shutting down.");
        }
        catch
        {
            operation.Complete(
                EntitySyncPlanStatuses.Failed,
                timeProvider.GetUtcNow(),
                "Apply failed.");
        }
    }

    private static string Key(string tenantId, string planId) => tenantId.Trim() + "\n" + planId.Trim();

    private sealed class ApplyOperation
    {
        private const int MaximumFailureSummaries = 25;
        private readonly object gate = new();
        private EntitySyncApplySnapshot snapshot;
        private Task? task;

        public ApplyOperation(string planId, int total, DateTimeOffset startedAt)
        {
            snapshot = new EntitySyncApplySnapshot(
                planId,
                EntitySyncPlanStatuses.Applying,
                total,
                0,
                0,
                0,
                0,
                startedAt,
                null,
                Array.Empty<EntitySyncApplyFailure>(),
                null);
        }

        public EntitySyncApplySnapshot Snapshot
        {
            get
            {
                lock (gate) return snapshot;
            }
        }

        public void Start(Task backgroundTask)
        {
            lock (gate) task = backgroundTask;
            _ = backgroundTask.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        public void ReportProgress(EntitySyncApplyProgress progress)
        {
            lock (gate)
            {
                var failures = snapshot.Failures;
                if (!progress.Item.Success
                    && !progress.Item.Skipped
                    && failures.Count < MaximumFailureSummaries)
                {
                    var updated = new EntitySyncApplyFailure[failures.Count + 1];
                    for (var index = 0; index < failures.Count; index++) updated[index] = failures[index];
                    updated[^1] = new EntitySyncApplyFailure(
                        progress.Item.Action,
                        progress.Item.Source,
                        progress.Item.Target,
                        progress.Item.Message);
                    failures = Array.AsReadOnly(updated);
                }

                snapshot = snapshot with
                {
                    Total = progress.Total,
                    Processed = progress.Processed,
                    Succeeded = progress.Succeeded,
                    Failed = progress.Failed,
                    Skipped = progress.Skipped,
                    Failures = failures
                };
            }
        }

        public void Complete(string status, DateTimeOffset completedAt, string? error)
        {
            lock (gate)
            {
                snapshot = snapshot with
                {
                    Status = status,
                    CompletedAt = completedAt,
                    Error = error
                };
            }
        }
    }
}
