using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncScheduleRepository
{
    Task InsertVersionAsync(
        string tenantId,
        EntitySyncSchedule schedule,
        CancellationToken cancellationToken);

    Task<EntitySyncSchedule?> GetAsync(
        string tenantId,
        Guid scheduleId,
        int version,
        CancellationToken cancellationToken);

    Task<EntitySyncSchedule?> GetLatestAsync(
        string tenantId,
        Guid scheduleId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncSchedule>> ListDueAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maximumRows,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncSchedule>> ListLatestAsync(
        string tenantId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntitySyncSchedule>>([]);

    Task InsertChangeEventAsync(
        string tenantId,
        EntitySyncCanonicalChangeEvent changeEvent,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncCanonicalChangeEvent>> ListPendingChangeEventsAsync(
        string tenantId,
        int maximumRows,
        CancellationToken cancellationToken);

    Task<bool> TrySetChangeEventStatusAsync(
        string tenantId,
        Guid eventId,
        EntitySyncCanonicalChangeStatus expectedStatus,
        EntitySyncCanonicalChangeStatus status,
        CancellationToken cancellationToken);
}

public sealed record SyncScheduleRunReceipt(
    Guid WorkId,
    Guid ScheduleId,
    int ScheduleVersion,
    DateTimeOffset QueuedAt);

public interface ISyncScheduleRunQueue
{
    Task<SyncScheduleRunReceipt?> TryEnqueueAsync(
        string tenantId,
        Guid scheduleId,
        int expectedVersion,
        Guid workId,
        EntitySyncActor requestedBy,
        CancellationToken cancellationToken);
}
