using System.Runtime.ExceptionServices;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public interface IEntitySyncScheduledRun
{
    Task<EntitySyncSchedulerStatusSnapshot> RunAsync(CancellationToken cancellationToken);
}

public sealed class EntitySyncScheduledRun : IEntitySyncScheduledRun
{
    private const int InspectionPageSize = 100;
    private readonly IEntitySyncSchedulerRunLock runLock;
    private readonly IServerManagedEntityAdapterFactory adapterFactory;
    private readonly IConnectionRuntimeFactory connections;
    private readonly IEntitySyncPlanRepository plans;
    private readonly EntitySyncService service;
    private readonly EntitySyncSchedulerStatus status;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntitySyncScheduledRun> logger;

    public EntitySyncScheduledRun(
        EntitySyncSchedulerOptions options,
        IEntitySyncSchedulerRunLock runLock,
        IServerManagedEntityAdapterFactory adapterFactory,
        IConnectionRuntimeFactory connections,
        IEntitySyncPlanRepository plans,
        EntitySyncService service,
        EntitySyncSchedulerStatus status,
        TimeProvider timeProvider,
        ILogger<EntitySyncScheduledRun>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.runLock = runLock ?? throw new ArgumentNullException(nameof(runLock));
        this.adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<EntitySyncScheduledRun>.Instance;
    }

    public async Task<EntitySyncSchedulerStatusSnapshot> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        string? planId = null;
        var aggregate = PlanAggregate.Empty;
        ApplyAggregate? progress = null;
        var stage = RunStage.AcquireLock;
        status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, progress, null));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changeStateScope = adapterFactory.GetNetSuiteHaloChangeStateScope();
            var routeKey = $"{EntitySyncSchedulerOptions.TenantId}|{changeStateScope}";
            var acquiredLease = await runLock
                .TryAcquireAsync(routeKey, cancellationToken)
                .ConfigureAwait(false);
            if (acquiredLease is null)
            {
                var skipped = CreateStatus(
                    "SkippedOverlap",
                    startedAt,
                    timeProvider.GetUtcNow(),
                    null,
                    PlanAggregate.Empty,
                    null,
                    null);
                status.Publish(skipped);
                return skipped;
            }
            await using var lease = new CancellationPreservingLease(
                acquiredLease,
                cancellationToken,
                logger);


            stage = RunStage.VendorConnections;
            await using var source = await AcquireCurrentAdapterAsync(
                EntitySyncSchedulerOptions.SourceVendor,
                EntitySyncSchedulerOptions.SourceConnectionId,
                cancellationToken).ConfigureAwait(false);
            await using var target = await AcquireCurrentAdapterAsync(
                EntitySyncSchedulerOptions.TargetVendor,
                EntitySyncSchedulerOptions.TargetConnectionId,
                cancellationToken).ConfigureAwait(false);
            var sourceProbe = await ProbeConnectionAsync(source.Adapter, cancellationToken).ConfigureAwait(false);
            var targetProbe = await ProbeConnectionAsync(target.Adapter, cancellationToken).ConfigureAwait(false);
            var probeException = sourceProbe.Exception ?? targetProbe.Exception;
            if (probeException is not null)
                ExceptionDispatchInfo.Capture(probeException).Throw();
            if (!sourceProbe.Connected || !targetProbe.Connected)
                throw new InvalidOperationException("A scheduled vendor connection test failed.");

            stage = RunStage.Planning;
            var createdPlan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
            {
                TenantId = EntitySyncSchedulerOptions.TenantId,
                SourceVendor = EntitySyncSchedulerOptions.SourceVendor,
                SourceConnectionId = EntitySyncSchedulerOptions.SourceConnectionId,
                SourceEntityType = EntitySyncSchedulerOptions.SourceEntityType,
                TargetVendor = EntitySyncSchedulerOptions.TargetVendor,
                TargetConnectionId = EntitySyncSchedulerOptions.TargetConnectionId,
                TargetEntityType = EntitySyncSchedulerOptions.TargetEntityType,
                IncludeInactive = true,
                CreateMissing = false,
                UpdatePolicy = EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
                ChangeStateScope = changeStateScope
            }, cancellationToken).ConfigureAwait(false);
            planId = createdPlan.Id;

            stage = RunStage.PlanValidation;
            var inspectedDigest = InspectEveryPage(planId);
            var snapshot = plans.Get(EntitySyncSchedulerOptions.TenantId, planId);
            ValidateFixedRoute(snapshot, changeStateScope);
            if (!EntitySyncPlanDigest.Compute(snapshot).Equals(inspectedDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("The plan changed after inspection.");
            aggregate = ValidateAndAggregate(snapshot);
            status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, null, null));

            stage = RunStage.Approval;
            var approvedDigest = service.ApprovePlan(
                EntitySyncSchedulerOptions.TenantId,
                planId,
                inspectedDigest);
            if (!approvedDigest.Equals(inspectedDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("The approved digest did not exactly match the inspected digest.");

            stage = RunStage.Apply;
            var applyResult = await service.ApplyAsync(
                EntitySyncSchedulerOptions.TenantId,
                planId,
                apply: true,
                cancellationToken,
                update =>
                {
                    progress = new ApplyAggregate(update.Succeeded, update.Failed, update.Skipped);
                    status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, progress, null));
                }).ConfigureAwait(false);

            var terminal = CreateStatus(
                applyResult.Success ? "Applied" : "Failed",
                startedAt,
                timeProvider.GetUtcNow(),
                planId,
                aggregate,
                new ApplyAggregate(
                    applyResult.Succeeded,
                    applyResult.Failed,
                    applyResult.Skipped),
                applyResult.Success ? null : "Synchronization apply completed with failures.");
            status.Publish(terminal);
            return terminal;
        }
        catch (OperationCanceledException)
        {
            var cancelled = CreateStatus(
                "Failed",
                startedAt,
                timeProvider.GetUtcNow(),
                planId,
                aggregate,
                progress,
                "Scheduled synchronization was cancelled.");
            status.Publish(cancelled);
            throw;
        }
        catch (Exception exception)
        {
            var safeError = SafeError(stage);
            var failed = CreateStatus(
                "Failed",
                startedAt,
                timeProvider.GetUtcNow(),
                planId,
                aggregate,
                progress,
                safeError);
            status.Publish(failed);
            logger.LogError(
                "Scheduled synchronization failed at {Stage}. ExceptionType={ExceptionType}; Message={ErrorMessage}",
                stage,
                exception.GetType().Name,
                safeError);
            return failed;
        }
    }

    private static async Task<ConnectionProbe> ProbeConnectionAsync(
        IEntityAdapter adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ConnectionProbe(
                await adapter.TestConnectionAsync(cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ConnectionProbe(false, exception);
        }
    }

    private Task<IConnectionRuntimeLease> AcquireCurrentAdapterAsync(
        string vendor,
        string connectionId,
        CancellationToken cancellationToken) =>
        connections.AcquireCurrentAsync(
            EntitySyncSchedulerOptions.TenantId,
            vendor,
            connectionId,
            cancellationToken);

    private string InspectEveryPage(string planId)
    {
        string? stableDigest = null;
        var totalItems = -1;
        var inspectedItems = 0;
        var pageNumber = 1;

        do
        {
            var page = service.GetPlan(
                EntitySyncSchedulerOptions.TenantId,
                planId,
                pageNumber,
                InspectionPageSize);
            if (page.Page != pageNumber || page.PageSize != InspectionPageSize)
                throw new InvalidOperationException("Plan inspection returned an unexpected page.");
            if (totalItems < 0) totalItems = page.TotalItems;
            else if (page.TotalItems != totalItems)
                throw new InvalidOperationException("Plan size changed during inspection.");
            if (stableDigest is null) stableDigest = page.Digest;
            else if (!page.Digest.Equals(stableDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("Plan digest changed during inspection.");
            if (page.Items.Count == 0 && inspectedItems < totalItems)
                throw new InvalidOperationException("Plan inspection ended before all items were returned.");

            inspectedItems += page.Items.Count;
            if (inspectedItems > totalItems)
                throw new InvalidOperationException("Plan inspection returned more items than expected.");
            pageNumber++;
        }
        while (inspectedItems < totalItems);

        if (stableDigest is null || inspectedItems != totalItems)
            throw new InvalidOperationException("Plan inspection was incomplete.");
        return stableDigest;
    }

    private static void ValidateFixedRoute(EntitySyncPlan plan, string changeStateScope)
    {
        if (!plan.TenantId.Equals(EntitySyncSchedulerOptions.TenantId, StringComparison.Ordinal)
            || !plan.SourceVendor.Equals(EntitySyncSchedulerOptions.SourceVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.SourceEntityType.Equals(EntitySyncSchedulerOptions.SourceEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetVendor.Equals(EntitySyncSchedulerOptions.TargetVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetEntityType.Equals(EntitySyncSchedulerOptions.TargetEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.Execution.SourceConnectionId.Equals(EntitySyncSchedulerOptions.SourceConnectionId, StringComparison.Ordinal)
            || !plan.Execution.TargetConnectionId.Equals(EntitySyncSchedulerOptions.TargetConnectionId, StringComparison.Ordinal)
            || plan.Execution.MatchOptions.CreateMissing
            || plan.Execution.UpdatePolicy != EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly
            || !string.Equals(plan.Execution.ChangeStateScope, changeStateScope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The plan does not match the fixed scheduled route.");
        }
    }

    private static PlanAggregate ValidateAndAggregate(EntitySyncPlan plan)
    {
        var changed = 0;
        var unchanged = 0;
        var policySkipped = 0;
        foreach (var item in plan.Items)
        {
            if (item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase))
            {
                if (!item.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
                    || item.Target is null
                    || item.DesiredStateHashVersion != EntityWriteRequestDigest.SchemaVersion
                    || !IsLowercaseSha256(item.DesiredStateHash))
                {
                    throw new InvalidOperationException("A writable plan item failed scheduled update validation.");
                }
                changed++;
                continue;
            }

            if (!item.Action.Equals("None", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The plan contains a prohibited scheduled action.");
            if (item.MatchType.Equals("Unchanged", StringComparison.OrdinalIgnoreCase)) unchanged++;
            else policySkipped++;
        }

        return new PlanAggregate(plan.Items.Count, changed, unchanged, policySkipped);
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static EntitySyncSchedulerStatusSnapshot CreateStatus(
        string state,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? planId,
        PlanAggregate aggregate,
        ApplyAggregate? progress,
        string? error) => new(
            state,
            startedAt,
            completedAt,
            null,
            planId,
            aggregate.Total,
            aggregate.Changed,
            aggregate.Unchanged,
            aggregate.PolicySkipped,
            progress?.Succeeded ?? 0,
            progress?.Failed ?? 0,
            progress?.Skipped ?? 0,
            error);

    private static string SafeError(RunStage stage) => stage switch
    {
        RunStage.AcquireLock => "Scheduler run lock initialization failed.",
        RunStage.VendorConnections => "Vendor connection setup or validation failed.",
        RunStage.Planning or RunStage.PlanValidation or RunStage.Approval =>
            "Synchronization plan validation failed.",
        RunStage.Apply => "Synchronization apply failed.",
        _ => "Scheduled synchronization failed."
    };

    private enum RunStage
    {
        AcquireLock,
        VendorConnections,
        Planning,
        PlanValidation,
        Approval,
        Apply
    }

    private sealed class CancellationPreservingLease(
        IAsyncDisposable inner,
        CancellationToken cancellationToken,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Scheduler run-lock cleanup failed during cancellation. ExceptionType={ExceptionType}; Message={ErrorMessage}",
                    exception.GetType().Name,
                    "Scheduler run-lock cleanup failed.");
            }
        }
    }

    private sealed record ConnectionProbe(bool Connected, Exception? Exception);

    private sealed record ApplyAggregate(int Succeeded, int Failed, int Skipped);

    private sealed record PlanAggregate(int Total, int Changed, int Unchanged, int PolicySkipped)
    {
        public static PlanAggregate Empty { get; } = new(0, 0, 0, 0);
    }
}
