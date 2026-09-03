using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IIdempotencyRepository
{
    Task<bool> TryInsertAsync(
        string tenantId,
        EntitySyncIdempotencyReceipt receipt,
        CancellationToken cancellationToken);

    Task<EntitySyncIdempotencyReceipt?> GetAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<bool> TryCompleteAsync(
        string tenantId,
        string idempotencyKey,
        EntitySyncSha256 requestSha256,
        int responseStatusCode,
        EntitySyncJsonValue responseBody,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(
        string tenantId,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken);
}
