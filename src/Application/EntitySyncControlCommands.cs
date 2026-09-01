using System.Security.Cryptography;
using System.Text;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record DurablePlanCommandResult(
    DurablePlanResult Result,
    EntitySyncDurablePlan Plan);

public interface IEntitySyncControlCommands
{
    Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListConnectionsAsync(
        string tenantId,
        CancellationToken cancellationToken);

    Task<DurablePlanCommandResult> CreatePlanAsync(
        CreateDurablePlanRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken);
    Task<EntitySyncDurablePlan> ImportPlanAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken);


    Task<DurablePlanInspectionPage> InspectPlanAsync(
        string tenantId,
        Guid planId,
        int page,
        int pageSize,
        EntitySyncActor actor,
        CancellationToken cancellationToken);

    Task<DurablePlanApprovalResult> ApprovePlanAsync(
        string tenantId,
        Guid planId,
        string digest,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation> QueueDryRunAsync(
        string tenantId,
        Guid planId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation> QueueApplyAsync(
        string tenantId,
        Guid planId,
        Guid approvalId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken);
}

public sealed class EntitySyncControlCommands(
    ConnectionDefinitionService connections,
    DurablePlanService plans,
    IDurableSyncPlanRepository planRepository,
    SyncOperationService operations,
    EntityExclusionService exclusions) : IEntitySyncControlCommands
{
    public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListConnectionsAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        connections.ListAsync(tenantId, vendor: null, enabled: null, cancellationToken);

    public async Task<DurablePlanCommandResult> CreatePlanAsync(
        CreateDurablePlanRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        var result = await plans.CreatePlanAsync(request, actor, cancellationToken)
            .ConfigureAwait(false);
        var persisted = await planRepository.GetAsync(
            result.TenantId, result.PlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The committed plan is unavailable.");
        return new DurablePlanCommandResult(result, persisted);
    }
    public Task<EntitySyncDurablePlan> ImportPlanAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        plans.ImportManifestAsync(
            tenantId, manifest, idempotencyKey, actor, cancellationToken);


    public Task<DurablePlanInspectionPage> InspectPlanAsync(
        string tenantId,
        Guid planId,
        int page,
        int pageSize,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        plans.GetPageAsync(tenantId, planId, page, pageSize, actor, cancellationToken);

    public async Task<DurablePlanApprovalResult> ApprovePlanAsync(
        string tenantId,
        Guid planId,
        string digest,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        var approvalId = StableGuid(idempotencyKey.Trim());
        return await plans.RecoverControlApprovalAsync(
                tenantId, planId, digest, approvalId, cancellationToken)
                .ConfigureAwait(false)
            ?? await plans.ApproveControlAsync(
                tenantId, planId, digest, actor, approvalId, cancellationToken)
                .ConfigureAwait(false);
    }

    public Task<EntitySyncOperation> QueueDryRunAsync(
        string tenantId,
        Guid planId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        operations.QueueDryRunAsync(
            tenantId, planId, idempotencyKey, actor, cancellationToken);

    public Task<EntitySyncOperation> QueueApplyAsync(
        string tenantId,
        Guid planId,
        Guid approvalId,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        operations.QueueApplyAsync(
            tenantId, planId, approvalId, idempotencyKey, actor, cancellationToken);

    public Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken) =>
        exclusions.ListAsync(request, cancellationToken);

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
