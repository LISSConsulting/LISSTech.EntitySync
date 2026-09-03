using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public enum ControlWakeReason { Notification, Fallback }

public sealed record EntitySyncControlOptions(
    IReadOnlyList<string> TenantIds,
    TimeSpan LeaseDuration,
    TimeSpan HeartbeatInterval,
    TimeSpan RetryInterval)
{
    public static EntitySyncControlOptions FromEnvironment(
        EntitySyncWorkerSettings? workerSettings = null)
    {
        var configured = Environment.GetEnvironmentVariable("ENTITYSYNC_TENANT_IDS");
        var tenants = string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        if (tenants.Length == 0)
        {
            throw new InvalidOperationException(
                "ENTITYSYNC_TENANT_IDS must contain at least one tenant " +
                "[ENTITYSYNC_CONFIG_REQUIRED].");
        }
        if (tenants.Length > 100)
        {
            throw new InvalidOperationException(
                "ENTITYSYNC_TENANT_IDS cannot contain more than 100 tenants " +
                "[ENTITYSYNC_CONFIG_TENANT_LIMIT].");
        }

        var worker = workerSettings
            ?? EntitySyncWorkerSettings.FromCurrentEnvironment();
        return new EntitySyncControlOptions(
            tenants,
            worker.LeaseDuration,
            worker.HeartbeatInterval,
            worker.RetryInterval);
    }
}

public sealed class EntitySyncControlWorker : BackgroundService
{
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan routeLeaseDuration;
    private readonly PostgresSyncWorkQueue queue;
    private readonly IEntitySyncRouteLock routeLock;
    private readonly ISyncPolicyRepository policies;
    private readonly IConnectionDefinitionRepository connectionDefinitions;
    private readonly IConnectionRuntimeFactory connections;
    private readonly DurablePlanService durablePlans;
    private readonly SyncOperationService operationService;
    private readonly EntitySyncOperationWorker operationWorker;
    private readonly TimeProvider timeProvider;
    private readonly EntitySyncControlOptions options;
    private readonly ILogger<EntitySyncControlWorker> logger;
    private readonly string owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public EntitySyncControlWorker(
        PostgresSyncWorkQueue queue,
        IEntitySyncRouteLock routeLock,
        ISyncPolicyRepository policies,
        IConnectionDefinitionRepository connectionDefinitions,
        IConnectionRuntimeFactory connections,
        DurablePlanService durablePlans,
        SyncOperationService operationService,
        EntitySyncOperationWorker operationWorker,
        TimeProvider timeProvider,
        EntitySyncControlOptions options,
        ILogger<EntitySyncControlWorker>? logger = null)
    {
        this.queue = queue;
        this.routeLock = routeLock;
        this.policies = policies;
        this.connectionDefinitions = connectionDefinitions;
        this.connections = connections;
        this.durablePlans = durablePlans;
        this.operationService = operationService;
        this.operationWorker = operationWorker;
        this.timeProvider = timeProvider;
        this.options = options;
        this.logger = logger ?? NullLogger<EntitySyncControlWorker>.Instance;
        routeLeaseDuration = options.LeaseDuration;
    }

    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var tenantId in options.TenantIds)
            count += await queue.EnqueueDueAsync(tenantId, 100, cancellationToken)
                .ConfigureAwait(false);
        return count;
    }

    public async Task<bool> ExecuteOneAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in options.TenantIds)
        {
            if (await ExecuteControlOneAsync(tenantId, cancellationToken).ConfigureAwait(false))
                return true;
            if (await operationWorker.ExecuteOneAsync(
                    tenantId, owner + ":operation", cancellationToken).ConfigureAwait(false)
                is not null)
                return true;
        }
        return false;
    }

    public static Task<ControlWakeReason> WaitForWorkAsync(
        IEntitySyncWorkSignal signal,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        WaitForWorkAsync(signal, timeProvider, FallbackInterval, cancellationToken);

    public static async Task<ControlWakeReason> WaitForWorkAsync(
        IEntitySyncWorkSignal signal,
        TimeProvider timeProvider,
        TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        if (retryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var notificationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var fallbackCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var notification = signal.WaitAsync(notificationCancellation.Token);
        var fallback = Task.Delay(
            retryInterval, timeProvider, fallbackCancellation.Token);
        var completed = await Task.WhenAny(notification, fallback).ConfigureAwait(false);
        if (completed == notification)
        {
            fallbackCancellation.Cancel();
            await notification.ConfigureAwait(false);
            return ControlWakeReason.Notification;
        }
        notificationCancellation.Cancel();
        await fallback.ConfigureAwait(false);
        return ControlWakeReason.Fallback;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            RunHeartbeatLoopAsync(stoppingToken),
            RunEnqueueLoopAsync(stoppingToken),
            RunExecutionLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.RecordHeartbeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "EntitySync worker heartbeat failed.");
            }

            await Task.Delay(
                options.HeartbeatInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunEnqueueLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "EntitySync due-work enqueue failed.");
            }
            await WaitForWorkAsync(
                queue, timeProvider, options.RetryInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunExecutionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool executed;
            try
            {
                executed = await ExecuteOneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                executed = false;
                logger.LogError(exception, "EntitySync durable work execution failed.");
            }
            if (!executed)
                await WaitForWorkAsync(
                    queue, timeProvider, options.RetryInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ExecuteControlOneAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var work = await queue.TryLeaseNextAsync(
            tenantId, owner + ":control", options.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (work is null) return false;
        await using var route = await routeLock.TryAcquireAsync(
            work.TenantId, work.RouteScope, owner + ":route",
            routeLeaseDuration, cancellationToken).ConfigureAwait(false);
        if (route is null)
        {
            await queue.TryReleaseAsync(work, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (!await queue.TryStartPlanningAsync(work, cancellationToken).ConfigureAwait(false))
            return true;
        work = work with { State = SyncControlWorkState.Planning };
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = ownership.Token;
        var renewal = MaintainOwnershipAsync(work, route, ownership);
        try
        {
            var actor = new EntitySyncActor("entitysync-control-worker");
            EntitySyncDurablePlan? persistedPlan = null;
            if (work.PlanId is not null)
            {
                if (work.PlanDigestSha256 is null)
                    return await HoldAsync(
                        work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                        .ConfigureAwait(false);
                persistedPlan = await durablePlans.GetControlPlanAsync(
                    work.TenantId, work.PlanId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsExpectedPlan(work, persistedPlan))
                    return await HoldAsync(
                        work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                        .ConfigureAwait(false);
                if (work.OperationId is not null
                    || persistedPlan!.Status == EntitySyncDurablePlanStatus.Consumed)
                    return await CompleteCommittedOperationAsync(
                        work, persistedPlan!, actor, cancellationToken)
                        .ConfigureAwait(false);
            }

            var policy = await policies.GetAsync(
                work.TenantId, work.PolicyId, work.PolicyVersion, cancellationToken)
                .ConfigureAwait(false);
            var latest = await policies.GetLatestAsync(
                work.TenantId, work.PolicyId, cancellationToken).ConfigureAwait(false);
            if (policy is null || latest is null || !policy.Enabled
                || latest.Version != policy.Version
                || !policy.Definition.ScheduledApplySafeSubset
                || policy.Definition.UpdatePolicy
                    != EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly)
                return await HoldAsync(work, "POLICY_NOT_SCHEDULE_SAFE", cancellationToken)
                    .ConfigureAwait(false);

            string? sourceEntityId = null;
            CanonicalEntityVersion? pinnedCanonicalSource = null;
            if (work.Kind == SyncControlWorkKind.CanonicalChange && work.PlanId is null)
            {
                var sourceDefinition = await connectionDefinitions.GetAsync(
                    work.TenantId, policy.Definition.SourceConnectionId, cancellationToken)
                    .ConfigureAwait(false);
                if (sourceDefinition is null || !sourceDefinition.Enabled)
                    return await HoldAsync(work, "SOURCE_CONNECTION_UNAVAILABLE", cancellationToken)
                        .ConfigureAwait(false);
                await using var sourceLease = await connections.AcquireAsync(
                    work.TenantId, sourceDefinition.ConnectionId,
                    sourceDefinition.Generation, cancellationToken).ConfigureAwait(false);
                if (sourceLease.Adapter is not ICanonicalEntityVersionAdapter versioned)
                    return await HoldAsync(work, "CANONICAL_VERSION_READER_UNAVAILABLE", cancellationToken)
                        .ConfigureAwait(false);
                var canonical = new CanonicalChangeRequest(
                    work.TenantId,
                    work.CanonicalEventId!.Value.ToString("N"),
                    work.CanonicalEntityType!,
                    work.CanonicalEntityId!.Value,
                    work.CanonicalVersion!.Value,
                    work.ChangedFields,
                    work.PayloadSha256!,
                    timeProvider.GetUtcNow());
                var read = await CanonicalChangeService.ReadAssertedVersionAsync(
                    versioned, canonical, cancellationToken).ConfigureAwait(false);
                if (read.Status != CanonicalVersionReadStatus.Exact || read.Entity is null)
                    return await HoldAsync(
                        work, "CANONICAL_" + read.Status.ToString().ToUpperInvariant(),
                        cancellationToken).ConfigureAwait(false);
                sourceEntityId = work.CanonicalEntityId.Value.ToString("D");
                pinnedCanonicalSource = new CanonicalEntityVersion(
                    work.CanonicalEntityId.Value, work.CanonicalVersion.Value, read.Entity);
            }

            DurablePlanResult plan;
            if (work.PlanId is null)
            {
                plan = await durablePlans.CreatePlanAsync(
                    new CreateDurablePlanRequest
                    {
                        TenantId = work.TenantId,
                        IdempotencyKey = $"control-work:{work.WorkId:N}",
                        PolicyId = work.PolicyId,
                        PolicyVersion = work.PolicyVersion,
                        SourceEntityId = sourceEntityId,
                        PinnedCanonicalOnly = pinnedCanonicalSource is not null,
                        PinnedCanonicalSources =
                            PinnedCanonicalSourcesFor(pinnedCanonicalSource),
                        PlanLifetime = TimeSpan.FromHours(4)
                    },
                    actor,
                    cancellationToken).ConfigureAwait(false);
                if (plan.ItemCount == 0)
                    return await HoldAsync(work, "NO_PLAN_ITEMS", cancellationToken)
                        .ConfigureAwait(false);
                var planDigest = new EntitySyncSha256(plan.Digest);
                if (!await queue.TryCheckpointPlanAsync(
                        work, plan.PlanId, planDigest, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Control work lost its plan checkpoint fence.");
                work = work with { PlanId = plan.PlanId, PlanDigestSha256 = planDigest };
            }
            else if (work.PlanDigestSha256 is null)
            {
                return await HoldAsync(
                    work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                    .ConfigureAwait(false);
            }

            persistedPlan ??= await durablePlans.GetControlPlanAsync(
                work.TenantId, work.PlanId!.Value, cancellationToken).ConfigureAwait(false);
            if (!IsExpectedPlan(work, persistedPlan))
                return await HoldAsync(
                    work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                    .ConfigureAwait(false);
            var activePlan = persistedPlan!;
            plan = ToPlanResult(activePlan);


            Guid approvalId;
            if (work.ApprovalId is not null)
            {
                if (activePlan.Status is not (
                    EntitySyncDurablePlanStatus.Approved
                    or EntitySyncDurablePlanStatus.Consumed))
                    return await HoldAsync(
                        work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                        .ConfigureAwait(false);
                var recovered = await durablePlans.RecoverControlApprovalAsync(
                    work.TenantId, plan.PlanId, plan.Digest, work.ApprovalId.Value,
                    cancellationToken).ConfigureAwait(false);
                if (recovered is null)
                    return await HoldAsync(
                        work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                        .ConfigureAwait(false);
                approvalId = recovered.ApprovalId;
            }
            else if (activePlan.Status == EntitySyncDurablePlanStatus.Approved)
            {
                approvalId = PostgresSyncWorkQueue.CreateControlApprovalId(work.WorkId);
                var recovered = await durablePlans.RecoverControlApprovalAsync(
                    work.TenantId, plan.PlanId, plan.Digest, approvalId, cancellationToken)
                    .ConfigureAwait(false);
                if (recovered is null
                    || !await queue.TryCheckpointApprovalAsync(
                        work, approvalId, cancellationToken).ConfigureAwait(false))
                    return await HoldAsync(
                        work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                        .ConfigureAwait(false);
                work = work with { ApprovalId = approvalId };
            }
            else if (activePlan.Status == EntitySyncDurablePlanStatus.Draft)
            {
                var items = new List<EntitySyncDurablePlanItem>(plan.ItemCount);
                for (var page = 1; page <= plan.PageCount(100); page++)
                {
                    var inspected = await durablePlans.GetPageAsync(
                        work.TenantId, plan.PlanId, page, 100, actor, cancellationToken)
                        .ConfigureAwait(false);
                    items.AddRange(inspected.Items);
                }
                if (!PostgresSyncWorkQueue.IsSafeSubset(policy, items))
                    return await HoldAsync(work, "UNSAFE_PLAN_HELD", cancellationToken)
                        .ConfigureAwait(false);
                approvalId = PostgresSyncWorkQueue.CreateControlApprovalId(work.WorkId);
                var approved = await durablePlans.ApproveControlAsync(
                    work.TenantId, plan.PlanId, plan.Digest, actor,
                    approvalId, cancellationToken).ConfigureAwait(false);
                approvalId = approved.ApprovalId;
                if (!await queue.TryCheckpointApprovalAsync(
                        work, approvalId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Control work lost its approval checkpoint fence.");
                work = work with { ApprovalId = approvalId };
            }
            else
            {
                return await HoldAsync(
                    work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                    .ConfigureAwait(false);
            }

            var queuedOperation = await operationService.QueueApplyAsync(
                work.TenantId, plan.PlanId, approvalId,
                $"control-work:{work.WorkId:N}:apply", work.WorkId, actor,
                cancellationToken).ConfigureAwait(false);
            if (!await queue.TryCheckpointOperationAsync(
                    work, queuedOperation.OperationId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Control work lost its operation checkpoint fence.");
            work = work with { OperationId = queuedOperation.OperationId };
            if (!await queue.TryCompleteAsync(
                    work, plan.PlanId, approvalId, queuedOperation.OperationId,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Control work lost its owner/attempt/lease completion fence.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DurablePlanApprovalConflictException
                or SyncOperationIdempotencyConflictException)
        {
            logger.LogWarning(
                exception,
                "EntitySync control work {WorkId} has contradictory durable checkpoints.",
                work.WorkId);
            return await HoldAsync(
                work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "EntitySync control work {WorkId} will resume after its fenced lease expires.",
                work.WorkId);
            return true;
        }
        finally
        {
            await ownership.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ownership.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<bool> CompleteCommittedOperationAsync(
        SyncControlWork work,
        EntitySyncDurablePlan plan,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        if (plan.Status != EntitySyncDurablePlanStatus.Consumed
            || work.ApprovalId is null)
            return await HoldAsync(
                work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var approval = await durablePlans.RecoverControlApprovalAsync(
                work.TenantId,
                plan.PlanId,
                plan.PlanDigestSha256.Value,
                work.ApprovalId.Value,
                cancellationToken).ConfigureAwait(false);
            if (approval is null)
                return await HoldAsync(
                    work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                    .ConfigureAwait(false);
            var operation = await operationService.QueueApplyAsync(
                work.TenantId,
                plan.PlanId,
                approval.ApprovalId,
                $"control-work:{work.WorkId:N}:apply",
                work.WorkId,
                actor,
                cancellationToken).ConfigureAwait(false);
            if (work.OperationId is not null
                && work.OperationId != operation.OperationId)
                return await HoldAsync(
                    work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                    .ConfigureAwait(false);
            if (work.OperationId is null)
            {
                if (!await queue.TryCheckpointOperationAsync(
                        work, operation.OperationId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Control work lost its operation checkpoint fence.");
                work = work with { OperationId = operation.OperationId };
            }
            if (!await queue.TryCompleteAsync(
                    work, plan.PlanId, approval.ApprovalId, operation.OperationId,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Control work lost its owner/attempt/lease completion fence.");
            return true;
        }
        catch (DurablePlanApprovalConflictException)
        {
            return await HoldAsync(
                work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncOperationIdempotencyConflictException)
        {
            return await HoldAsync(
                work, "CONTROL_WORK_CHECKPOINT_CONFLICT", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<CanonicalEntityVersion> PinnedCanonicalSourcesFor(
        CanonicalEntityVersion? source) =>
        source is null ? [] : [source];

    private static bool IsExpectedPlan(
        SyncControlWork work,
        EntitySyncDurablePlan? plan) =>
        plan is not null
        && plan.PlanId == work.PlanId
        && plan.PlanDigestSha256 == work.PlanDigestSha256
        && plan.PolicyId == work.PolicyId
        && plan.PolicyVersion == work.PolicyVersion
        && plan.RouteScope.Equals(
            work.RouteScope, StringComparison.OrdinalIgnoreCase);

    private static DurablePlanResult ToPlanResult(EntitySyncDurablePlan plan) =>
        new(
            plan.TenantId,
            plan.PlanId,
            plan.PlanDigestSha256.Value,
            plan.ItemCount,
            plan.PolicyId,
            plan.PolicyVersion,
            plan.SourceConnectionGeneration,
            plan.TargetConnectionGeneration,
            plan.CreatedAt,
            plan.ExpiresAt);


    private async Task MaintainOwnershipAsync(
        SyncControlWork work,
        IEntitySyncRouteLease route,
        CancellationTokenSource ownership)
    {
        try
        {
            var interval = TimeSpan.FromTicks(
                Math.Min(options.LeaseDuration.Ticks, routeLeaseDuration.Ticks) / 3);
            while (!ownership.IsCancellationRequested)
            {
                await Task.Delay(interval, timeProvider, ownership.Token).ConfigureAwait(false);
                var workRenewed = await queue.TryRenewAsync(
                    work, options.LeaseDuration, ownership.Token)
                    .ConfigureAwait(false);
                if (!workRenewed)
                {
                    await ownership.CancelAsync().ConfigureAwait(false);
                    return;
                }
                var routeRenewed = await route.TryRenewAsync(
                    routeLeaseDuration, ownership.Token).ConfigureAwait(false);
                if (!routeRenewed)
                {
                    await ownership.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await ownership.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> HoldAsync(
        SyncControlWork work,
        string reason,
        CancellationToken cancellationToken)
    {
        await queue.TryHoldAsync(work, reason, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
