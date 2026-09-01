using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncAuditRepository
{
    Task AppendAsync(
        string tenantId,
        EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? fullValues,
        CancellationToken cancellationToken);
    async Task<bool> TryAppendAsync(
        string tenantId,
        EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? fullValues,
        CancellationToken cancellationToken)
    {
        await AppendAsync(tenantId, auditEvent, fullValues, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }


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
