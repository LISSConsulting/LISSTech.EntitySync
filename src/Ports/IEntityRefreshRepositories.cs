using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityRefreshStateRepository
{
    Task<IReadOnlyList<EntityRefreshStateSnapshot>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityRefreshDueWork>> LeaseDueAsync(
        string tenantId,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken);

    Task<EntityRefreshStateSnapshot> UpsertOnQueueAsync(
        string tenantId,
        EntityRefreshStateSnapshot state,
        DateTimeOffset nextScheduledAt,
        CancellationToken cancellationToken);

    /// <summary>
    Task<EntityRefreshStateSnapshot?> EnsureScheduledAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records an incremental event (atomic Upsert/Delete) without advancing the
    /// scheduled full-sweep cadence. Inserts a new Pending/Succeeded row when absent;
    /// updates the cursor/sourceUpdatedAt and last attempt timestamp when present.
    /// </summary>
    Task<EntityRefreshStateSnapshot?> UpsertIncrementalAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset receivedAt,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        CancellationToken cancellationToken);
    Task<EntityRefreshStateSnapshot?> TryAcquireLeaseAsync(
        EntityRefreshStateKey key,
        long expectedGeneration,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task<bool> TryRenewLeaseAsync(
        EntityRefreshStateKey key,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<EntityRefreshStateSnapshot?> TryReleaseLeaseAsync(
        EntityRefreshStateKey key,
        string owner,
        EntityRefreshStatus status,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset? lastSuccessfulAt,
        DateTimeOffset nextScheduledAt,
        long observedCount,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        string? errorCode,
        DateTimeOffset? snapshotStartedAt,
        DateTimeOffset? snapshotCompletedAt,
        CancellationToken cancellationToken);

    Task<bool> MarkStaleAsync(
        EntityRefreshStateKey key,
        long observedGeneration,
        bool isStale,
        CancellationToken cancellationToken);
}

public sealed record EntityRefreshDueWork(
    EntityRefreshStateSnapshot State,
    long ConnectionGeneration);

public interface IEntityRefreshEventRepository
{
    Task<IReadOnlyList<EntityRefreshEvent>> ListAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        int maximumRows,
        CancellationToken cancellationToken);

    Task AppendAsync(
        EntityRefreshEvent eventRecord,
        CancellationToken cancellationToken);
}

public interface IEntityRefreshCapabilityRepository
{
    Task ReplaceAsync(
        string tenantId,
        string connectionId,
        IReadOnlyList<EntityRefreshCapability> capabilities,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityRefreshCapability>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityRefreshCapability>> ListRefreshableAsync(
        string tenantId,
        CancellationToken cancellationToken);
}
