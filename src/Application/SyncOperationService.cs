using System.Security.Cryptography;
using System.Text;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class SyncOperationService(
    IDurableSyncPlanRepository plans,
    ISyncOperationRepository operations,
    ISyncPolicyRepository policies,
    IConnectionDefinitionRepository connectionDefinitions,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(365);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<EntitySyncOperation> QueueDryRunAsync(
        string tenantId,
        Guid planId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        QueueAsync(
            tenantId, planId, null, idempotencyKey, actor,
            EntitySyncOperationMode.DryRun, cancellationToken);

    public Task<EntitySyncOperation> QueueApplyAsync(
        string tenantId,
        Guid planId,
        Guid approvalId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        QueueAsync(
            tenantId, planId, approvalId, idempotencyKey, actor,
            EntitySyncOperationMode.Apply, cancellationToken);

    public static EntitySyncOperationStatus DeriveTerminalStatus(
        IReadOnlyCollection<EntitySyncItemOutcome> outcomes,
        bool cancellationRequestedBeforeDispatch = false)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (cancellationRequestedBeforeDispatch)
            return EntitySyncOperationStatus.Cancelled;
        if (outcomes.Count == 0
            || outcomes.Any(outcome => outcome == EntitySyncItemOutcome.Pending))
            throw new InvalidOperationException("Terminal status requires terminal item outcomes.");
        if (outcomes.All(outcome => outcome is EntitySyncItemOutcome.Succeeded or EntitySyncItemOutcome.Skipped))
            return EntitySyncOperationStatus.Succeeded;
        var succeeded = outcomes.Any(
            outcome => outcome == EntitySyncItemOutcome.Succeeded);
        return succeeded ? EntitySyncOperationStatus.Partial : EntitySyncOperationStatus.Failed;
    }

    private async Task<EntitySyncOperation> QueueAsync(
        string tenantId,
        Guid planId,
        Guid? approvalId,
        string idempotencyKey,
        EntitySyncActor actor,
        EntitySyncOperationMode mode,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        idempotencyKey = Require(idempotencyKey, nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(actor);
        if (planId == Guid.Empty) throw new ArgumentException("Plan ID is required.", nameof(planId));
        if (mode == EntitySyncOperationMode.Apply
            && (approvalId is null || approvalId == Guid.Empty))
            throw new ArgumentException("Approval ID is required for apply.", nameof(approvalId));

        var plan = await plans.GetAsync(tenantId, planId, cancellationToken).ConfigureAwait(false)
            ?? throw new DurablePlanNotFoundException(planId);
        var requestSha256 = EntitySyncCanonicalDigest.Compute(new
        {
            TenantId = tenantId,
            Mode = mode.ToString(),
            PlanId = plan.PlanId,
            PlanDigestSha256 = plan.PlanDigestSha256.Value,
            ApprovalId = approvalId,
            IdempotencyKey = idempotencyKey,
            ActorId = actor.ActorId
        });
        var operationId = StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey,
            RequestSha256 = requestSha256.Value
        }));
        var replay = await operations.FindByIdempotencyKeyAsync(
            tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null
            && replay.PlanId == plan.PlanId
            && replay.Mode == mode
            && replay.ApprovalId == approvalId
            && replay.RequestSha256 == requestSha256)
            return replay;
        if (replay is not null)
            throw new SyncOperationIdempotencyConflictException(idempotencyKey);
        var now = clock.GetUtcNow();
        if (mode == EntitySyncOperationMode.Apply
            && plan.Status != EntitySyncDurablePlanStatus.Approved)
            throw new DurablePlanApprovalConflictException(planId);
        if (mode == EntitySyncOperationMode.DryRun
            && plan.Status is EntitySyncDurablePlanStatus.Consumed or EntitySyncDurablePlanStatus.Expired)
            throw new InvalidOperationException("Only an unexpired unconsumed plan can be queued for dry-run.");

        await ValidateCurrentControlStateAsync(tenantId, plan, cancellationToken)
            .ConfigureAwait(false);
        EntitySyncApproval? approval = null;
        if (mode == EntitySyncOperationMode.Apply)
        {
            approval = await plans.GetApprovalAsync(
                tenantId, approvalId!.Value, cancellationToken).ConfigureAwait(false);
            if (approval is null
                || approval.PlanId != plan.PlanId
                || approval.PlanDigestSha256 != plan.PlanDigestSha256
                || approval.SourceConnectionId != plan.SourceConnectionId
                || approval.SourceConnectionGeneration != plan.SourceConnectionGeneration
                || approval.TargetConnectionId != plan.TargetConnectionId
                || approval.TargetConnectionGeneration != plan.TargetConnectionGeneration)
                throw new DurablePlanApprovalConflictException(planId);
        }

        var operation = (mode == EntitySyncOperationMode.Apply
            ? EntitySyncOperation.QueueApply(
                tenantId, operationId, plan.PlanId, approvalId, idempotencyKey,
                plan.RouteScope, plan.SourceConnectionId, plan.SourceConnectionGeneration,
                plan.TargetConnectionId, plan.TargetConnectionGeneration, now)
            : EntitySyncOperation.QueueDryRun(
                tenantId, operationId, plan.PlanId, idempotencyKey, plan.RouteScope,
                plan.SourceConnectionId, plan.SourceConnectionGeneration,
                plan.TargetConnectionId, plan.TargetConnectionGeneration, now)) with
        {
            RequestSha256 = requestSha256,
            TotalCount = plan.ItemCount
        };
        var items = await BuildOperationItemsAsync(
            tenantId, plan, operationId, now + SnapshotRetention, cancellationToken)
            .ConfigureAwait(false);

        if (mode == EntitySyncOperationMode.DryRun)
        {
            if (await operations.TryInsertAsync(tenantId, operation, items, cancellationToken)
                    .ConfigureAwait(false))
                return operation;
        }
        else if (await plans.TryConsumeApprovalAsync(
                     tenantId, approval!.ApprovalId, approval.InspectionId, plan.PlanId,
                     plan.PlanDigestSha256, plan.SourceConnectionId,
                     plan.SourceConnectionGeneration, plan.TargetConnectionId,
                     plan.TargetConnectionGeneration, operation, items, now,
                     cancellationToken).ConfigureAwait(false))
        {
            return operation;
        }

        var racedExisting = await operations.FindByIdempotencyKeyAsync(
            tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (racedExisting is not null
            && racedExisting.PlanId == plan.PlanId
            && racedExisting.Mode == mode
            && racedExisting.ApprovalId == approvalId
            && racedExisting.RequestSha256 == requestSha256)
            return racedExisting;
        if (racedExisting is not null)
            throw new SyncOperationIdempotencyConflictException(idempotencyKey);
        throw new DurablePlanApprovalConflictException(planId);
    }

    private async Task ValidateCurrentControlStateAsync(
        string tenantId,
        EntitySyncDurablePlan plan,
        CancellationToken cancellationToken)
    {
        var latest = await policies.GetLatestAsync(
            tenantId, plan.PolicyId, cancellationToken).ConfigureAwait(false);
        if (latest is null
            || !latest.Enabled
            || latest.Version != plan.PolicyVersion
            || latest.DefinitionSha256 != plan.PolicyDefinitionSha256)
            throw new DurablePlanPolicyChangedException(plan.PlanId);
        await ValidateConnectionAsync(
            tenantId, plan.SourceConnectionId, plan.SourceConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
        await ValidateConnectionAsync(
            tenantId, plan.TargetConnectionId, plan.TargetConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateConnectionAsync(
        string tenantId,
        string connectionId,
        long generation,
        CancellationToken cancellationToken)
    {
        var definition = await connectionDefinitions.GetAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || !definition.Enabled || definition.Generation != generation)
            throw new DurablePlanConnectionChangedException(connectionId);
    }

    private async Task<IReadOnlyList<EntitySyncOperationItem>> BuildOperationItemsAsync(
        string tenantId,
        EntitySyncDurablePlan plan,
        Guid operationId,
        DateTimeOffset snapshotsExpireAt,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var result = new List<EntitySyncOperationItem>(plan.ItemCount);
        for (var pageNumber = 1; result.Count < plan.ItemCount; pageNumber++)
        {
            var page = await plans.GetPageAsync(
                tenantId, plan.PlanId, pageNumber, pageSize, cancellationToken)
                .ConfigureAwait(false);
            if (page.Items.Count == 0)
                throw new InvalidOperationException("The durable plan item graph is incomplete.");
            foreach (var item in page.Items)
            {
                result.Add(new EntitySyncOperationItem(
                    tenantId, operationId, plan.PlanId, item.ItemId,
                    item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
                    item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
                    item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
                    item.Action, item.RedactedBefore, item.RedactedDesired,
                    item.BeforePayloadSha256, item.DesiredPayloadSha256, null,
                    snapshotsExpireAt, null, EntitySyncItemOutcome.Pending,
                    null, null, null, null));
            }
        }
        if (result.Count != plan.ItemCount)
            throw new InvalidOperationException("The durable plan item graph changed while queueing.");
        return result;
    }

    private static Guid StableGuid(EntitySyncSha256 digest)
    {
        var bytes = Convert.FromHexString(digest.Value);
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}

public sealed class SyncOperationIdempotencyConflictException(string idempotencyKey)
    : InvalidOperationException(
        $"Idempotency key '{idempotencyKey}' is already bound to a different operation request.");
