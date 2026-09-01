using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public enum DispatchPreparationOutcome
{
    Prepared,
    AlreadyDispatchStarted,
    Excluded,
    PolicyChanged,
    ConnectionChanged,
    StaleLease,
    NotFound
}

public sealed record DispatchPreparationResult(
    DispatchPreparationOutcome Outcome,
    EntitySyncOperationItem? Item);

public sealed record UnknownItemLease(
    EntitySyncOperationItem Item,
    int ReconciliationAttempt,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt);

public interface ISyncOperationRepository
{
    Task InsertAsync(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken);
    Task<bool> TryInsertAsync(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation?> FindByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken);


    Task<EntitySyncOperation?> GetAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncOperationItem>> GetItemsAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken);
    Task<EntitySyncOperationItem?> GetItemAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        CancellationToken cancellationToken);


    Task<EntitySyncOperation?> TryLeaseNextAsync(
        string tenantId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken);

    Task<bool> TryRenewLeaseAsync(
        string tenantId,
        Guid operationId,
        int expectedAttempt,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<bool> TryReplaceAsync(
        string tenantId,
        Guid operationId,
        EntitySyncOperationStatus expectedStatus,
        EntitySyncOperation replacement,
        CancellationToken cancellationToken);

    Task<bool> TryReplaceItemAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset now,
        EntitySyncItemOutcome expectedOutcome,
        EntitySyncOperationItem replacement,
        CancellationToken cancellationToken);
    Task<DispatchPreparationResult> TryPrepareDispatchAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        Guid policyId,
        int policyVersion,
        EntitySyncSha256 policyDefinitionSha256,
        EntitySyncOperationItem preparedItem,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<bool> TryRecordItemAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        EntitySyncItemOutcome expectedOutcome,
        EntitySyncOperationItem replacement,
        EntitySyncOperationItemSnapshot? snapshot,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation?> TryFinalizeAttemptAsync(
        string tenantId,
        Guid operationId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation?> TryCancelAttemptAsync(
        string tenantId,
        Guid operationId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<UnknownItemLease?> TryLeaseUnknownItemAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> TryRenewUnknownItemLeaseAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> TryRecordReconciliationEvidenceAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string leaseOwner,
        EntitySyncSha256 afterPayloadSha256,
        string? vendorTargetEntityId,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<bool> TryCompleteReconciliationAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string leaseOwner,
        EntitySyncOperationItem replacement,
        EntitySyncOperationItemSnapshot? snapshot,
        CancellationToken cancellationToken);
    Task<bool> TryCommitReconciliationSuccessAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string reconciliationLeaseOwner,
        EntitySyncOperationItem replacement,
        EntitySyncChangeState? checkpoint,
        EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? auditFullValues,
        CancellationToken cancellationToken);


    Task InsertSnapshotAsync(
        string tenantId,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<EntitySyncOperationItemSnapshot?> GetSnapshotAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredSnapshotsAsync(
        string tenantId,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken);
}
