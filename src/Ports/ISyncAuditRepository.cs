using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncAuditRepository
{
    Task AppendAsync(
        string tenantId,
        EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? fullValues,
        CancellationToken cancellationToken);

    Task<EntitySyncAuditPage> ListAsync(
        string tenantId,
        DateTimeOffset? continuationOccurredAt,
        Guid? continuationEventId,
        int pageSize,
        CancellationToken cancellationToken);

    Task<EntitySyncAuditEventFullValues?> GetFullValuesAsync(
        string tenantId,
        Guid auditEventId,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredFullValuesAsync(
        string tenantId,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken);
}
