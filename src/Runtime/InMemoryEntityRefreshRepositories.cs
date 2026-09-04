using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntityRefreshStateRepository : IEntityRefreshStateRepository
{
    private readonly object gate = new();
    private readonly Dictionary<EntityRefreshStateKey, EntityRefreshStateSnapshot> states = new();

    public Task<IReadOnlyList<EntityRefreshStateSnapshot>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<EntityRefreshStateSnapshot> snapshot = states.Values
                .Where(state => state.Key.TenantId.Equals(tenantId, StringComparison.Ordinal)
                    && state.Key.ConnectionId.Equals(connectionId, StringComparison.Ordinal)
                    && (entityType is null
                        || state.Key.EntityType.Equals(entityType, StringComparison.Ordinal)))
                .OrderBy(state => state.Key.EntityType, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public Task<IReadOnlyList<EntityRefreshDueWork>> LeaseDueAsync(
        string tenantId,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (maximumRows is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));

        var expires = now.Add(leaseDuration);
        var leased = new List<EntityRefreshDueWork>();
        lock (gate)
        {
            // Mirror the Postgres behavior: due rows are those whose scheduled time
            // has elapsed and are not leased by another active owner. Succeeded rows
            // are eligible for recurrence; Failed rows are eligible for retry;
            // Pending rows are eligible for first run.
            var due = states.Values
                .Where(state => state.Key.TenantId.Equals(tenantId, StringComparison.Ordinal))
                .Where(state => state.NextScheduledAt <= now)
                .Where(state => state.Status is EntityRefreshStatus.Pending
                    or EntityRefreshStatus.Failed
                    or EntityRefreshStatus.Succeeded)
                .Where(state => state.LeaseExpiresAt(now) is null)
                .OrderBy(state => state.NextScheduledAt)
                .Take(maximumRows)
                .ToArray();
            foreach (var existing in due)
            {
                var next = existing with
                {
                    Status = EntityRefreshStatus.Running,
                    LastAttemptAt = now,
                    // Preserve Manual mode for queued explicit refresh; otherwise
                    // the recurring sweep drives the row as Scheduled.
                    Mode = existing.Mode == EntityRefreshMode.Manual
                        ? EntityRefreshMode.Manual
                        : EntityRefreshMode.Scheduled,
                    SnapshotStartedAt = now,
                    SnapshotCompletedAt = null
                } with
                {
                    LeaseOwner = owner,
                    LeaseExpiresAt = expires
                };
                states[existing.Key] = next;
                leased.Add(new EntityRefreshDueWork(states[existing.Key], next.ConnectionGeneration));
            }
            return Task.FromResult<IReadOnlyList<EntityRefreshDueWork>>(leased);
        }
    }

    public Task<EntityRefreshStateSnapshot> UpsertOnQueueAsync(
        string tenantId,
        EntityRefreshStateSnapshot state,
        DateTimeOffset nextScheduledAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var key = new EntityRefreshStateKey(tenantId, state.Key.ConnectionId, state.Key.EntityType);
            if (states.TryGetValue(key, out var existing))
            {
                // Manual queue always flips the row to Pending unless another worker
                // currently holds an active lease. Mirrors the Postgres CASE.
                var activeLease = existing.LeaseExpiresAt(DateTimeOffset.UtcNow) is not null;
                var merged = existing with
                {
                    Vendor = state.Vendor,
                    ConnectionGeneration = state.ConnectionGeneration,
                    Mode = state.Mode,
                    NextScheduledAt = nextScheduledAt,
                    Status = activeLease ? existing.Status : EntityRefreshStatus.Pending,
                    IsStale = false
                };
                states[key] = merged;
                return Task.FromResult(merged);
            }
            var snapshot = state with
            {
                Key = key,
                Status = EntityRefreshStatus.Pending,
                NextScheduledAt = nextScheduledAt,
                IsStale = false
            };
            states[key] = snapshot;
            return Task.FromResult(snapshot);
        }
    }

    public Task<EntityRefreshStateSnapshot?> EnsureScheduledAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var key = new EntityRefreshStateKey(tenantId, definition.ConnectionId, entityType);
            if (states.TryGetValue(key, out var existing))
            {
                return Task.FromResult<EntityRefreshStateSnapshot?>(existing);
            }
            var snapshot = new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = definition.Vendor,
                ConnectionGeneration = definition.Generation,
                Status = EntityRefreshStatus.Pending,
                Mode = EntityRefreshMode.Scheduled,
                NextScheduledAt = dueAt
            };
            states[key] = snapshot;
            return Task.FromResult<EntityRefreshStateSnapshot?>(snapshot);
        }
    }

    public Task<EntityRefreshStateSnapshot?> TryAcquireLeaseAsync(
        EntityRefreshStateKey key,
        long expectedGeneration,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        lock (gate)
        {
            if (!states.TryGetValue(key, out var existing))
                return Task.FromResult<EntityRefreshStateSnapshot?>(null);
            if (existing.ConnectionGeneration != expectedGeneration)
                return Task.FromResult<EntityRefreshStateSnapshot?>(null);
            if (existing.LeaseExpiresAt(startedAt) is { } expiry
                && expiry > startedAt
                && !string.Equals(existing.LeaseOwner(startedAt), owner, StringComparison.Ordinal))
                return Task.FromResult<EntityRefreshStateSnapshot?>(null);
            var snapshot = existing with
            {
                Status = EntityRefreshStatus.Running,
                SnapshotStartedAt = startedAt,
                SnapshotCompletedAt = null,
                LastAttemptAt = startedAt
            } with
            {
                LeaseOwner = owner,
                LeaseExpiresAt = startedAt.Add(leaseDuration)
            };
            states[key] = snapshot;
            return Task.FromResult<EntityRefreshStateSnapshot?>(snapshot);
        }
    }

    public Task<bool> TryRenewLeaseAsync(
        EntityRefreshStateKey key,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!states.TryGetValue(key, out var existing)) return Task.FromResult(false);
            if (!string.Equals(existing.LeaseOwner(now), owner, StringComparison.Ordinal))
                return Task.FromResult(false);
            states[key] = existing with { LeaseExpiresAt = now.Add(leaseDuration) };
            return Task.FromResult(true);
        }
    }

    public Task<EntityRefreshStateSnapshot?> TryReleaseLeaseAsync(
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
        CancellationToken cancellationToken)
    {
        if (observedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(observedCount));

        lock (gate)
        {
            if (!states.TryGetValue(key, out var existing)) return Task.FromResult<EntityRefreshStateSnapshot?>(null);
            if (!string.Equals(existing.LeaseOwner(lastAttemptAt), owner, StringComparison.Ordinal))
                return Task.FromResult<EntityRefreshStateSnapshot?>(null);
            var snapshot = existing with
            {
                Status = status,
                Mode = existing.Mode,
                LastAttemptAt = lastAttemptAt,
                LastSuccessfulAt = lastSuccessfulAt ?? existing.LastSuccessfulAt,
                NextScheduledAt = nextScheduledAt,
                ObservedCount = observedCount,
                Cursor = cursor ?? existing.Cursor,
                SourceUpdatedAt = sourceUpdatedAt ?? existing.SourceUpdatedAt,
                ErrorCode = errorCode,
                SnapshotStartedAt = snapshotStartedAt ?? existing.SnapshotStartedAt,
                SnapshotCompletedAt = snapshotCompletedAt ?? existing.SnapshotCompletedAt,
                IsStale = status switch
                {
                    EntityRefreshStatus.Failed => true,
                    EntityRefreshStatus.Succeeded => false,
                    _ => existing.IsStale
                }
            } with
            {
                LeaseOwner = null,
                LeaseExpiresAt = null
            };
            states[key] = snapshot;
            return Task.FromResult<EntityRefreshStateSnapshot?>(snapshot);
        }
    }

    public Task<bool> MarkStaleAsync(
        EntityRefreshStateKey key,
        long observedGeneration,
        bool isStale,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!states.TryGetValue(key, out var existing)) return Task.FromResult(false);
            if (existing.ConnectionGeneration != observedGeneration) return Task.FromResult(false);
            states[key] = existing with { IsStale = isStale };
            return Task.FromResult(true);
        }
    }

    public Task<EntityRefreshStateSnapshot?> UpsertIncrementalAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset receivedAt,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var key = new EntityRefreshStateKey(tenantId, definition.ConnectionId, entityType);
            if (states.TryGetValue(key, out var existing))
            {
                var updated = existing with
                {
                    Status = EntityRefreshStatus.Succeeded,
                    Mode = EntityRefreshMode.Incremental,
                    LastAttemptAt = receivedAt,
                    Cursor = cursor ?? existing.Cursor,
                    SourceUpdatedAt = sourceUpdatedAt ?? existing.SourceUpdatedAt,
                    ObservedCount = existing.ObservedCount + 1
                };
                states[key] = updated;
                return Task.FromResult<EntityRefreshStateSnapshot?>(updated);
            }
            var snapshot = new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = definition.Vendor,
                ConnectionGeneration = definition.Generation,
                Status = EntityRefreshStatus.Succeeded,
                Mode = EntityRefreshMode.Incremental,
                LastAttemptAt = receivedAt,
                NextScheduledAt = DateTimeOffset.UtcNow + EntityRefreshConstants.DefaultRefreshInterval,
                Cursor = cursor,
                SourceUpdatedAt = sourceUpdatedAt,
                ObservedCount = 1
            };
            states[key] = snapshot;
            return Task.FromResult<EntityRefreshStateSnapshot?>(snapshot);
        }
    }
}

public static class EntityRefreshStateExtensions
{
    public static string? LeaseOwner(this EntityRefreshStateSnapshot snapshot, DateTimeOffset now) =>
        snapshot.LeaseExpiresAt is { } expiry && expiry > now ? snapshot.LeaseOwner : null;

    public static DateTimeOffset? LeaseExpiresAt(this EntityRefreshStateSnapshot snapshot, DateTimeOffset now) =>
        snapshot.LeaseExpiresAt is { } expiry && expiry > now ? expiry : null;
}

public sealed class InMemoryEntityRefreshEventRepository : IEntityRefreshEventRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, EntityRefreshEvent> events = new();

    public Task<IReadOnlyList<EntityRefreshEvent>> ListAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (maximumRows is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        lock (gate)
        {
            IReadOnlyList<EntityRefreshEvent> matched = events.Values
                .Where(value => value.Key.TenantId.Equals(tenantId, StringComparison.Ordinal)
                    && value.Key.ConnectionId.Equals(connectionId, StringComparison.Ordinal)
                    && (entityType is null
                        || value.Key.EntityType.Equals(entityType, StringComparison.Ordinal)))
                .OrderByDescending(value => value.ReceivedAt)
                .Take(maximumRows)
                .ToArray();
            return Task.FromResult(matched);
        }
    }

    public Task AppendAsync(
        EntityRefreshEvent eventRecord,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            events[eventRecord.EventId] = eventRecord;
            return Task.CompletedTask;
        }
    }
}

public sealed class InMemoryEntityRefreshCapabilityRepository : IEntityRefreshCapabilityRepository
{
    private readonly object gate = new();
    private readonly Dictionary<string, EntityRefreshCapability> capabilities = new(StringComparer.Ordinal);

    public Task ReplaceAsync(
        string tenantId,
        string connectionId,
        IReadOnlyList<EntityRefreshCapability> rows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var prefix = $"{tenantId}|{connectionId}|";
            foreach (var key in capabilities.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                capabilities.Remove(key);
            foreach (var capability in rows)
            {
                capabilities[$"{capability.TenantId}|{capability.ConnectionId}|{capability.EntityType}"] =
                    capability with { LastDiscoveredAt = now };
            }
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<EntityRefreshCapability>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var prefix = $"{tenantId}|{connectionId}|";
            IReadOnlyList<EntityRefreshCapability> matched = capabilities.Values
                .Where(value => value.TenantId.Equals(tenantId, StringComparison.Ordinal)
                    && value.ConnectionId.Equals(connectionId, StringComparison.Ordinal))
                .OrderBy(value => value.EntityType, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(matched);
        }
    }

    public Task<IReadOnlyList<EntityRefreshCapability>> ListRefreshableAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<EntityRefreshCapability> matched = capabilities.Values
                .Where(value => value.TenantId.Equals(tenantId, StringComparison.Ordinal)
                    && value.SupportsRefresh)
                .OrderBy(value => value.ConnectionId, StringComparer.Ordinal)
                .ThenBy(value => value.EntityType, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(matched);
        }
    }
}
