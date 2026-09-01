using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IDurableSyncPlanRepository
{
    Task<DurablePlanCreationClaim> TryClaimCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid proposedOwnerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);


    Task ReleaseCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken);

    Task InsertClaimedAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken);


    Task InsertAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        CancellationToken cancellationToken);

    Task<EntitySyncDurablePlan?> GetAsync(
        string tenantId,
        Guid planId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncDurablePlan>> ListAsync(
        string tenantId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntitySyncDurablePlan>>([]);

    Task<EntitySyncDurablePlanPage> GetPageAsync(
        string tenantId,
        Guid planId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<EntitySyncInspectionSession> GetOrOpenInspectionAsync(
        string tenantId,
        Guid proposedInspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<EntitySyncInspectionSession?> FindInspectionAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        EntitySyncActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncInspectionRange>> ListInspectionRangesAsync(
        string tenantId,
        Guid inspectionId,
        CancellationToken cancellationToken);

    Task<EntitySyncInspectionRange> RecordInspectionRangeAsync(
        string tenantId,
        Guid inspectionId,
        Guid rangeId,
        int rangeStart,
        int rangeEnd,
        DateTimeOffset inspectedAt,
        CancellationToken cancellationToken);

    Task<EntitySyncInspectionSession> CompleteInspectionAsync(
        string tenantId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<bool> HasCompleteInspectionAsync(
        string tenantId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        CancellationToken cancellationToken);

    Task<EntitySyncApproval> ApproveInspectionAsync(
        string tenantId,
        Guid approvalId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncActor actor,
        DateTimeOffset approvedAt,
        DateTimeOffset? expiresAt,
        EntitySyncAuditEvent auditEvent,
        CancellationToken cancellationToken);

    Task<EntitySyncApproval?> GetApprovalAsync(
        string tenantId,
        Guid approvalId,
        CancellationToken cancellationToken) =>
        Task.FromResult<EntitySyncApproval?>(null);

    Task<bool> TryConsumeApprovalAsync(
        string tenantId,
        Guid approvalId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncOperation applyOperation,
        IReadOnlyList<EntitySyncOperationItem> operationItems,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<bool> TryExpireAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        EntitySyncDurablePlanStatus expectedStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public enum DurablePlanCreationClaimState
{
    Owner,
    Waiting,
    Completed,
    Conflict
}

public sealed record DurablePlanCreationClaim(
    DurablePlanCreationClaimState State,
    Guid? OwnerToken,
    DateTimeOffset? LeaseExpiresAt,
    Guid? ResultPlanId,
    EntitySyncSha256? ResultPlanDigestSha256);
