using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
    private readonly EntitySyncSchedulerOptions options;
    private readonly IEntitySyncSchedulerRunLock runLock;
    private readonly IServerManagedEntityAdapterFactory adapterFactory;
    private readonly IEntityConnectionRepository connections;
    private readonly IEntitySyncPlanRepository plans;
    private readonly EntitySyncService service;
    private readonly EntitySyncSchedulerStatus status;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntitySyncScheduledRun> logger;

    public EntitySyncScheduledRun(
        EntitySyncSchedulerOptions options,
        IEntitySyncSchedulerRunLock runLock,
        IServerManagedEntityAdapterFactory adapterFactory,
        IEntityConnectionRepository connections,
        IEntitySyncPlanRepository plans,
        EntitySyncService service,
        EntitySyncSchedulerStatus status,
        TimeProvider timeProvider,
        ILogger<EntitySyncScheduledRun>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
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
        var progress = ApplyAggregate.Empty;
        var stage = RunStage.AcquireLock;
        status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, progress, null));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopedRoutes = options.Routes
                .Select(route => new ScopedRoute(
                    route,
                    adapterFactory.GetChangeStateScope(
                        route.SourceVendor,
                        route.SourceConnectionId,
                        route.SourceEntityType,
                        route.TargetVendor,
                        route.TargetConnectionId,
                        route.TargetEntityType)))
                .ToArray();
            var routeKey = $"{EntitySyncSchedulerOptions.TenantId}|{ChainLockScope(scopedRoutes)}";
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
                    ApplyAggregate.Empty,
                    null);
                status.Publish(skipped);
                return skipped;
            }
            await using var lease = new CancellationPreservingLease(
                acquiredLease,
                cancellationToken,
                logger);

            stage = RunStage.VendorConnections;
            var registrations = new List<EntityConnectionRegistration>();
            var endpoints = options.Routes
                .SelectMany(route => new[]
                {
                    new ConnectionEndpoint(route.SourceVendor, route.SourceConnectionId),
                    new ConnectionEndpoint(route.TargetVendor, route.TargetConnectionId)
                })
                .DistinctBy(
                    endpoint => $"{EntitySyncVendors.Normalize(endpoint.Vendor)}|{endpoint.ConnectionId}",
                    StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in endpoints)
            {
                registrations.Add(await RegisterFreshAdapterAsync(
                    endpoint.Vendor,
                    endpoint.ConnectionId,
                    cancellationToken).ConfigureAwait(false));
            }

            var probes = new List<ConnectionProbe>(registrations.Count);
            foreach (var registration in registrations)
            {
                probes.Add(await ProbeConnectionAsync(registration.Adapter, cancellationToken).ConfigureAwait(false));
            }
            var probeException = probes.Select(probe => probe.Exception).FirstOrDefault(exception => exception is not null);
            if (probeException is not null)
                ExceptionDispatchInfo.Capture(probeException).Throw();
            if (probes.Any(probe => !probe.Connected))
                throw new InvalidOperationException("A scheduled vendor connection test failed.");

            foreach (var scopedRoute in scopedRoutes)
            {
                var route = scopedRoute.Route;
                stage = RunStage.Planning;
                var createdPlan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
                {
                    TenantId = EntitySyncSchedulerOptions.TenantId,
                    SourceVendor = route.SourceVendor,
                    SourceConnectionId = route.SourceConnectionId,
                    SourceEntityType = route.SourceEntityType,
                    TargetVendor = route.TargetVendor,
                    TargetConnectionId = route.TargetConnectionId,
                    TargetEntityType = route.TargetEntityType,
                    IncludeInactive = true,
                    CreateMissing = false,
                    SourceExternalIdName = route.SourceExternalIdName,
                    UpdatePolicy = EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
                    ChangeStateScope = scopedRoute.ChangeStateScope
                }, cancellationToken).ConfigureAwait(false);
                planId = createdPlan.Id;

                stage = RunStage.PlanValidation;
                var inspectedDigest = InspectEveryPage(planId);
                var snapshot = plans.Get(EntitySyncSchedulerOptions.TenantId, planId);
                ValidateScheduledRoute(snapshot, route, scopedRoute.ChangeStateScope);
                if (!EntitySyncPlanDigest.Compute(snapshot).Equals(inspectedDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException("The plan changed after inspection.");
                aggregate = aggregate.Add(ValidateAndAggregate(snapshot));
                status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, progress, null));

                stage = RunStage.Approval;
                var approvedDigest = service.ApprovePlan(
                    EntitySyncSchedulerOptions.TenantId,
                    planId,
                    inspectedDigest);
                if (!approvedDigest.Equals(inspectedDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException("The approved digest did not exactly match the inspected digest.");

                stage = RunStage.Apply;
                var completedProgress = progress;
                var applyResult = await service.ApplyAsync(
                    EntitySyncSchedulerOptions.TenantId,
                    planId,
                    apply: true,
                    cancellationToken,
                    update =>
                    {
                        progress = completedProgress.Add(new ApplyAggregate(update.Succeeded, update.Failed, update.Skipped));
                        status.Publish(CreateStatus("Running", startedAt, null, planId, aggregate, progress, null));
                    }).ConfigureAwait(false);
                progress = completedProgress.Add(new ApplyAggregate(
                    applyResult.Succeeded,
                    applyResult.Failed,
                    applyResult.Skipped));
                if (!applyResult.Success)
                {
                    var failedApply = CreateStatus(
                        "Failed",
                        startedAt,
                        timeProvider.GetUtcNow(),
                        planId,
                        aggregate,
                        progress,
                        "Synchronization apply completed with failures.");
                    status.Publish(failedApply);
                    return failedApply;
                }
            }

            var terminal = CreateStatus(
                "Applied",
                startedAt,
                timeProvider.GetUtcNow(),
                planId,
                aggregate,
                progress,
                null);
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

    private async Task<EntityConnectionRegistration> RegisterFreshAdapterAsync(
        string vendor,
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var admission = connections.BeginRegistration(
            EntitySyncSchedulerOptions.TenantId,
            connectionId,
            vendor);
        IEntityAdapter? adapter = null;
        try
        {
            adapter = await adapterFactory.CreateAsync(vendor, null, cancellationToken).ConfigureAwait(false);
            var registration = connections.Register(
                EntitySyncSchedulerOptions.TenantId,
                connectionId,
                adapter);
            adapter = null;
            return registration;
        }
        finally
        {
            if (adapter is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (adapter is IDisposable disposable)
                disposable.Dispose();
        }
    }

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

    private static void ValidateScheduledRoute(
        EntitySyncPlan plan,
        EntitySyncSchedulerRoute route,
        string changeStateScope)
    {
        if (!plan.TenantId.Equals(EntitySyncSchedulerOptions.TenantId, StringComparison.Ordinal)
            || !plan.SourceVendor.Equals(route.SourceVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.SourceEntityType.Equals(route.SourceEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetVendor.Equals(route.TargetVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetEntityType.Equals(route.TargetEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.Execution.SourceConnectionId.Equals(route.SourceConnectionId, StringComparison.Ordinal)
            || !plan.Execution.TargetConnectionId.Equals(route.TargetConnectionId, StringComparison.Ordinal)
            || plan.Execution.MatchOptions.CreateMissing
            || plan.Execution.UpdatePolicy != EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly
            || !string.Equals(plan.Execution.ChangeStateScope, changeStateScope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The plan does not match its scheduled chain route.");
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

    private static string ChainLockScope(IReadOnlyList<ScopedRoute> routes)
    {
        if (routes.Count == 1) return routes[0].ChangeStateScope;
        var canonical = string.Join(
            "|",
            routes.Select(route =>
                $"{route.Route.SourceVendor}>{route.Route.TargetVendor}:{route.ChangeStateScope}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

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
    private sealed record ConnectionEndpoint(string Vendor, string ConnectionId);

    private sealed record ScopedRoute(EntitySyncSchedulerRoute Route, string ChangeStateScope);


    private sealed record ApplyAggregate(int Succeeded, int Failed, int Skipped)
    {
        public static ApplyAggregate Empty { get; } = new(0, 0, 0);

        public ApplyAggregate Add(ApplyAggregate other) => new(
            Succeeded + other.Succeeded,
            Failed + other.Failed,
            Skipped + other.Skipped);
    }

    private sealed record PlanAggregate(int Total, int Changed, int Unchanged, int PolicySkipped)
    {
        public static PlanAggregate Empty { get; } = new(0, 0, 0, 0);

        public PlanAggregate Add(PlanAggregate other) => new(
            Total + other.Total,
            Changed + other.Changed,
            Unchanged + other.Unchanged,
            PolicySkipped + other.PolicySkipped);
    }
}
