using System.Security.Cryptography;
using System.Text;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record EntityRefreshResult(
    EntityRefreshStateSnapshot State,
    long ObservedCount,
    long TombstonedCount,
    bool Failed);

public sealed class EntityRefreshService(
    IConnectionDefinitionRepository connections,
    IConnectionRuntimeFactory runtimes,
    IEntityGraphRepository graph,
    IEntityRefreshStateRepository states,
    IEntityRefreshEventRepository events,
    IEntityRefreshCapabilityRepository capabilities,
    TimeProvider timeProvider)
{
    // Configurable so long-running vendors (e.g. BILL) can keep their lease alive
    // across multi-minute full snapshots. The renewal loop fences the same owner
    // token and aborts cleanly if the row is ever taken by another worker.
    public TimeSpan RefreshLeaseDuration { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshLeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(2);

    public async Task<EntityRefreshStateSnapshot> RefreshAsync(
        string tenantId,
        string connectionId,
        string entityType,
        EntityRefreshMode mode,
        long? expectedGeneration = null,
        DateTimeOffset? nextScheduledAt = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await RequireConnectionAsync(
            tenantId, connectionId, expectedGeneration, cancellationToken)
            .ConfigureAwait(false);
        var snapshotKey = new EntityRefreshStateKey(tenantId, connectionId, entityType);
        var owner = GenerateOwner();
        var startedAt = timeProvider.GetUtcNow();
        var acquired = await states.TryAcquireLeaseAsync(
            snapshotKey, definition.Generation, owner, RefreshLeaseDuration,
            startedAt, cancellationToken).ConfigureAwait(false);
        if (acquired is null)
        {
            throw new InvalidOperationException(
                $"The refresh for connection '{connectionId}' and entity type '{entityType}' "
                + "is already leased by another worker.");
        }
        return await RefreshWithLeaseAsync(
            tenantId, definition, entityType, mode, acquired, nextScheduledAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntityRefreshStateSnapshot> RefreshWithLeaseAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        EntityRefreshMode mode,
        EntityRefreshStateSnapshot leasedState,
        DateTimeOffset? nextScheduledAt,
        CancellationToken cancellationToken)
    {
        var snapshotKey = leasedState.Key;
        var owner = leasedState.LeaseOwner
            ?? throw new InvalidOperationException(
                $"The leased state for {snapshotKey} does not carry an owner token.");
        await events.AppendAsync(new EntityRefreshEvent(
            Guid.NewGuid(),
            snapshotKey,
            definition.Vendor,
            mode,
            EntityRefreshEventOperation.SnapshotStarted,
            EntityRefreshStatus.Running,
            leasedState.SnapshotStartedAt,
            null,
            null,
            null,
            null,
            null,
            timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        // Background lease-renewal loop: a long snapshot (BILL can take >15min) must
        // not lose its lease. Renewal fences on the same owner token and aborts when
        // the row no longer recognizes us.
        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewalLoopAsync(snapshotKey, owner, renewalCts.Token);

        EntityRefreshResult result;
        try
        {
            result = await ExecuteRefreshAsync(
                tenantId, definition, entityType, mode,
                leasedState.SnapshotStartedAt ?? timeProvider.GetUtcNow(),
                leasedState.Cursor,
                leasedState.SourceUpdatedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            renewalCts.Cancel();
            await SafeAwaitAsync(renewalTask);
            return await FailAsync(
                leasedState, definition, mode, owner,
                EntityRefreshConstants.ErrorAdapterThrew, cancellationToken)
                .ConfigureAwait(false);
        }

        renewalCts.Cancel();
        await SafeAwaitAsync(renewalTask);

        var completedAt = timeProvider.GetUtcNow();
        var nextSchedule = nextScheduledAt ?? (completedAt + EntityRefreshConstants.DefaultRefreshInterval);
        // Persist the snapshot's cursor and source-updated-at — they reflect the
        // authoritative run that just completed, not the pre-lease leasedState
        // values, so the row tracks the latest authoritative boundary.
        var releasedState = await states.TryReleaseLeaseAsync(
            snapshotKey, owner, EntityRefreshStatus.Succeeded,
            completedAt, completedAt, nextSchedule,
            result.ObservedCount, result.State.Cursor, result.State.SourceUpdatedAt,
            null, leasedState.SnapshotStartedAt, completedAt,
            cancellationToken).ConfigureAwait(false);

        await events.AppendAsync(new EntityRefreshEvent(
            Guid.NewGuid(),
            snapshotKey,
            definition.Vendor,
            mode,
            EntityRefreshEventOperation.SnapshotCompleted,
            EntityRefreshStatus.Succeeded,
            leasedState.SnapshotStartedAt,
            completedAt,
            result.ObservedCount,
            result.State.Cursor,
            result.State.SourceUpdatedAt,
            null,
            completedAt), cancellationToken).ConfigureAwait(false);

        return releasedState ?? leasedState.With(
            status: EntityRefreshStatus.Succeeded,
            lastAttemptAt: completedAt,
            lastSuccessfulAt: completedAt,
            nextScheduledAt: nextSchedule,
            observedCount: result.ObservedCount,
            errorCode: null,
            snapshotCompletedAt: completedAt,
            cursor: result.State.Cursor,
            sourceUpdatedAt: result.State.SourceUpdatedAt);
    }
    private async Task<EntityRefreshStateSnapshot> FailAsync(
        EntityRefreshStateSnapshot leasedState,
        EntitySyncConnectionDefinition definition,
        EntityRefreshMode mode,
        string owner,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var failedAt = timeProvider.GetUtcNow();
        var nextRetry = failedAt + EntityRefreshConstants.DefaultRefreshInterval;
        var failedState = await states.TryReleaseLeaseAsync(
            leasedState.Key, owner, EntityRefreshStatus.Failed,
            failedAt, leasedState.LastSuccessfulAt, nextRetry,
            leasedState.ObservedCount, leasedState.Cursor,
            leasedState.SourceUpdatedAt, errorCode,
            leasedState.SnapshotStartedAt, failedAt,
            cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(new EntityRefreshEvent(
            Guid.NewGuid(),
            leasedState.Key,
            definition.Vendor,
            mode,
            EntityRefreshEventOperation.SnapshotFailed,
            EntityRefreshStatus.Failed,
            leasedState.SnapshotStartedAt,
            failedAt,
            null,
            null,
            null,
            errorCode,
            failedAt), cancellationToken).ConfigureAwait(false);
        return failedState ?? leasedState.With(
            status: EntityRefreshStatus.Failed,
            errorCode: errorCode,
            lastAttemptAt: failedAt,
            nextScheduledAt: nextRetry,
            snapshotCompletedAt: failedAt);
    }

    private async Task RenewalLoopAsync(
        EntityRefreshStateKey key,
        string owner,
        CancellationToken cancellationToken)
    {
        if (RefreshLeaseRenewalInterval <= TimeSpan.Zero
            || RefreshLeaseRenewalInterval >= RefreshLeaseDuration)
        {
            return; // Renewal disabled or nonsensical interval.
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RefreshLeaseRenewalInterval,
                    timeProvider, cancellationToken).ConfigureAwait(false);
                var renewed = await states.TryRenewLeaseAsync(
                    key, owner, RefreshLeaseDuration,
                    timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    // Lease has been taken by another worker; abandon the snapshot.
                    throw new InvalidOperationException(
                        $"The lease for {key} was lost during refresh; aborting snapshot.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    public async Task<EntityRefreshStateSnapshot> QueueAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        long expectedGeneration,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken = default)
    {
        var definition = await RequireConnectionAsync(
            tenantId, connectionId, expectedGeneration, cancellationToken)
            .ConfigureAwait(false);

        var scopes = await DiscoverEntityTypesAsync(
            tenantId, definition, cancellationToken).ConfigureAwait(false);
        var selected = entityType is null
            ? scopes
            : scopes.Where(scope => scope.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entityType is not null && selected.Count == 0)
            throw new ArgumentException(
                $"Entity type '{entityType}' is not refreshable for vendor '{definition.Vendor}'.",
                nameof(entityType));

        EntityRefreshStateSnapshot? last = null;
        foreach (var scope in selected)
        {
            var state = await states.UpsertOnQueueAsync(
                tenantId,
                new EntityRefreshStateSnapshot
                {
                    Key = new EntityRefreshStateKey(tenantId, connectionId, scope.EntityType),
                    Vendor = definition.Vendor,
                    ConnectionGeneration = definition.Generation,
                    Status = EntityRefreshStatus.Pending,
                    Mode = EntityRefreshMode.Manual
                },
                requestedAt,
                cancellationToken).ConfigureAwait(false);
            await events.AppendAsync(new EntityRefreshEvent(
                Guid.NewGuid(),
                state.Key,
                definition.Vendor,
                EntityRefreshMode.Manual,
                EntityRefreshEventOperation.QueueSnapshot,
                EntityRefreshStatus.Pending,
                null,
                null,
                null,
                null,
                null,
                null,
                requestedAt), cancellationToken).ConfigureAwait(false);
            last = state;
        }
        return last
            ?? throw new InvalidOperationException(
                $"No refreshable entity types were discovered for connection '{connectionId}'.");
    }

    public async Task<EntityAtomicEventOutcome> AcceptAtomicEventAsync(
        string tenantId,
        string connectionId,
        EntityAtomicEvent atomicEvent,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        var definition = await RequireConnectionAsync(
            tenantId, connectionId, expectedGeneration, cancellationToken)
            .ConfigureAwait(false);

        var scope = new EntityGraphScope(
            tenantId, definition.Vendor, connectionId, atomicEvent.EntityType);

        var outcome = await graph.ApplyAtomicEventAsync(
            scope, atomicEvent, definition.Generation, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Kind == EntityAtomicEventOutcomeKind.Applied)
        {
            var snapshotKey = new EntityRefreshStateKey(tenantId, connectionId, atomicEvent.EntityType);
            await events.AppendAsync(new EntityRefreshEvent(
                Guid.NewGuid(),
                snapshotKey,
                definition.Vendor,
                EntityRefreshMode.Incremental,
                atomicEvent.Operation == EntityAtomicOperation.Upsert
                    ? EntityRefreshEventOperation.AtomicUpsert
                    : EntityRefreshEventOperation.AtomicDelete,
                EntityRefreshStatus.Succeeded,
                null,
                null,
                1,
                atomicEvent.SourceCursor,
                atomicEvent.SourceUpdatedAt,
                null,
                outcome.AppliedAt), cancellationToken).ConfigureAwait(false);
            await states.UpsertIncrementalAsync(
                tenantId, definition, atomicEvent.EntityType,
                outcome.AppliedAt, atomicEvent.SourceCursor,
                atomicEvent.SourceUpdatedAt, cancellationToken).ConfigureAwait(false);
        }
        return outcome;
    }

    public async Task<int> DiscoverAndQueueDueAsync(
        string tenantId,
        DateTimeOffset now,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var owner = GenerateOwner();
        var due = (await states.LeaseDueAsync(tenantId, owner, RefreshLeaseDuration,
            now, 100, cancellationToken).ConfigureAwait(false)).ToArray();
        var processed = 0;
        foreach (var work in due)
        {
            var definition = await connections.GetAsync(
                tenantId, work.State.Key.ConnectionId, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                await states.TryReleaseLeaseAsync(
                    work.State.Key, owner, EntityRefreshStatus.Failed,
                    now, work.State.LastSuccessfulAt, now + interval,
                    0, work.State.Cursor, work.State.SourceUpdatedAt,
                    EntityRefreshConstants.ErrorConnectionUnavailable,
                    work.State.SnapshotStartedAt, now,
                    cancellationToken).ConfigureAwait(false);
                processed++;
                continue;
            }

            // Preserve Manual mode for queued explicit refreshes; recurring sweeps
            // default to Scheduled. Mode is owned by the leased row, not by us.
            var mode = work.State.Mode == EntityRefreshMode.Manual
                ? EntityRefreshMode.Manual
                : EntityRefreshMode.Scheduled;
            var nextSchedule = now + interval;
            try
            {
                var completed = await RefreshWithLeaseAsync(
                    tenantId, definition, work.State.Key.EntityType,
                    mode, work.State, (DateTimeOffset?)nextSchedule, cancellationToken)
                .ConfigureAwait(false);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Worker shutdown / tenant cancellation. Release the lease as Pending
                // so the row resurfaces on the next sweep with the current snapshot
                // cursor/source_updated_at preserved — we never tombstone on cancel.
                await states.TryReleaseLeaseAsync(
                    work.State.Key, owner, EntityRefreshStatus.Pending,
                    now, work.State.LastSuccessfulAt, nextSchedule,
                    work.State.ObservedCount, work.State.Cursor,
                    work.State.SourceUpdatedAt,
                    null,
                    work.State.SnapshotStartedAt, null,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await states.TryReleaseLeaseAsync(
                    work.State.Key, owner, EntityRefreshStatus.Failed,
                    now, work.State.LastSuccessfulAt, nextSchedule,
                    work.State.ObservedCount, work.State.Cursor,
                    work.State.SourceUpdatedAt,
                    EntityRefreshConstants.ErrorAdapterThrew,
                    work.State.SnapshotStartedAt, now,
                    cancellationToken).ConfigureAwait(false);
                processed++;
            }
        }
        return processed;
    }

    public async Task<IReadOnlyList<EntityRefreshCapability>> DiscoverEntityTypesAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        CancellationToken cancellationToken)
    {
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        var adapterCapabilities = await lease.Adapter.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        var discovered = adapterCapabilities.EntityTypes
            .Where(entityType => entityType.SupportsAction(EntityAdapterActions.Read))
            .Select(entityType => new EntityRefreshCapability(
                tenantId,
                definition.ConnectionId,
                definition.Vendor,
                entityType.EntityType,
                SupportsRefresh: true,
                timeProvider.GetUtcNow()))
            .ToArray();
        await capabilities.ReplaceAsync(
            tenantId, definition.ConnectionId, discovered, timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        // Ensure every discovered readable type has a Pending state row scheduled now.
        var dueAt = timeProvider.GetUtcNow();
        foreach (var scope in discovered)
        {
            await states.EnsureScheduledAsync(
                tenantId, definition, scope.EntityType, dueAt, cancellationToken)
                .ConfigureAwait(false);
        }
        return discovered;
    }

    private async Task<EntitySyncConnectionDefinition> RequireConnectionAsync(
        string tenantId,
        string connectionId,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("Connection ID is required.", nameof(connectionId));

        var definition = await connections.GetAsync(tenantId, connectionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
        if (expectedGeneration.HasValue && definition.Generation != expectedGeneration.Value)
            throw new ConnectionGenerationConflictException(connectionId, expectedGeneration.Value);
        return definition;
    }

    private async Task<EntityRefreshResult> ExecuteRefreshAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        EntityRefreshMode mode,
        DateTimeOffset snapshotStartedAt,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        CancellationToken cancellationToken)
    {
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        var adapterCapabilities = await lease.Adapter.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!adapterCapabilities.TryGetEntityType(entityType, out var entityTypeCapabilities))
            throw new NotSupportedException(
                $"Adapter for vendor '{definition.Vendor}' does not support entity type '{entityType}'.");
        if (!entityTypeCapabilities.SupportsAction(EntityAdapterActions.Read))
            throw new NotSupportedException(
                $"Adapter for vendor '{definition.Vendor}' does not support reads for entity type '{entityType}'.");

        // Authoritative complete traversal — adapters must yield every record for the
        // entity type. The service intentionally omits Count so adapters (especially
        // paginated vendors) cannot silently cap at 1000 and produce a partial sweep.
        var entities = await lease.Adapter.GetEntitiesAsync(new EntityQuery
        {
            EntityType = entityType,
            FullObjects = true
        }, cancellationToken).ConfigureAwait(false);

        var observedAt = timeProvider.GetUtcNow();
        var snapshot = new EntityGraphSnapshot(
            new EntityGraphScope(tenantId, definition.Vendor, definition.ConnectionId, entityType),
            definition.Generation,
            entities,
            snapshotStartedAt,
            observedAt,
            cursor,
            sourceUpdatedAt);
        var result = await graph.ReplaceAuthoritativeSnapshotAsync(
            snapshot, cancellationToken).ConfigureAwait(false);
        return new EntityRefreshResult(
            new EntityRefreshStateSnapshot
            {
                Key = new EntityRefreshStateKey(tenantId, definition.ConnectionId, entityType),
                Vendor = definition.Vendor,
                ConnectionGeneration = definition.Generation,
                Status = EntityRefreshStatus.Succeeded,
                Mode = mode,
                Cursor = cursor,
                SourceUpdatedAt = sourceUpdatedAt
            },
            result.ObservedCount,
            result.TombstonedCount,
            Failed: false);
    }

    private string GenerateOwner() =>
        $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public static string GenerateConnectionId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(28);
        sb.Append("cnx_");
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        ulong hi = BitConverter.ToUInt64(bytes[..8]);
        ulong lo = BitConverter.ToUInt64(bytes[8..]);
        for (var i = 0; i < 12; i++)
        {
            sb.Append(alphabet[(int)(hi & 0x1F)]);
            hi >>= 5;
        }
        for (var i = 0; i < 12; i++)
        {
            sb.Append(alphabet[(int)(lo & 0x1F)]);
            lo >>= 5;
        }
        return sb.ToString();
    }
}
