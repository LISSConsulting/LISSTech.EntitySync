using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncOperationRepository
{
    Task InsertAsync(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation?> GetAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncOperationItem>> GetItemsAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<EntitySyncOperation?> TryLeaseNextAsync(
        string tenantId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken);

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
        EntitySyncOperationItem replacement,
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
