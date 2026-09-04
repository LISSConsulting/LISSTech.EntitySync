using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntityGraphRepository : IEntityGraphRepository
{
    private readonly object gate = new();
    private readonly Dictionary<string, EntityGraphRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGraphRelationship> relationships = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> appliedAtomicEvents = new();
    // Per-connection generation tracker. The in-memory implementation cannot join
    // connection_definitions, so it observes and rotates generations in lock-step
    // with the in-memory connection repository for atomic-event guards.
    private readonly Dictionary<string, long> connectionGenerations = new(StringComparer.Ordinal);


    public Task ObserveEntitiesAsync(EntityGraphObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = EntityGraphPersistence.ValidateScope(observation.Scope);
        var planId = EntityGraphPersistence.Optional(observation.PlanId, 128);
        // Prefer the source-supplied timestamp when present so the graph stores the
        // vendor's notion of ordering instead of the worker's wall-clock arrival.
        var effectiveTimestamp = observation.SourceUpdatedAt ?? observation.ObservedAt;
        lock (gate)
        {
            foreach (var entity in observation.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = EntityGraphPersistence.Serialize(entity);
                var hash = EntityGraphPersistence.Hash(payload);
                var snapshot = EntityGraphPersistence.Deserialize(payload);
                var key = EntityGraphPersistence.ValidateKey(new EntityGraphNodeKey(
                    scope.TenantId,
                    scope.Vendor,
                    scope.ConnectionId,
                    scope.EntityType,
                    entity.Id));
                var identity = EntityGraphPersistence.Key(key);
                records.TryGetValue(identity, out var current);
                var firstObservedAt = current is null
                    ? effectiveTimestamp
                    : Earlier(current.FirstObservedAt, effectiveTimestamp);
                if (current is not null && effectiveTimestamp < current.LastObservedAt)
                {
                    records[identity] = current with { FirstObservedAt = firstObservedAt };
                    continue;
                }
                records[identity] = new EntityGraphRecord(
                    key,
                    snapshot,
                    hash,
                    firstObservedAt,
                    effectiveTimestamp,
                    planId ?? current?.LastPlanId);
            }
        }
        return Task.CompletedTask;
    }

    public Task ObserveRelationshipsAsync(
        IReadOnlyCollection<EntityGraphRelationshipObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var unvalidated in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = EntityGraphPersistence.ValidateRelationship(unvalidated);
                var sourceIdentity = EntityGraphPersistence.Key(observation.Source);
                var targetIdentity = EntityGraphPersistence.Key(observation.Target);
                if (!records.ContainsKey(sourceIdentity) || !records.ContainsKey(targetIdentity))
                    throw new InvalidOperationException("Both relationship endpoints must be observed before the relationship.");
                var identity = EntityGraphPersistence.RelationshipKey(observation);
                relationships.TryGetValue(identity, out var current);
                var firstObservedAt = current is null
                    ? observation.ObservedAt
                    : Earlier(current.FirstObservedAt, observation.ObservedAt);
                if (current is not null && observation.ObservedAt < current.LastObservedAt)
                {
                    relationships[identity] = current with { FirstObservedAt = firstObservedAt };
                    continue;
                }
                var preserveConfirmed = current?.Status == EntityGraphRelationshipStatuses.Confirmed
                    && observation.Status == EntityGraphRelationshipStatuses.Proposed;
                var status = preserveConfirmed ? current!.Status : observation.Status;
                relationships[identity] = new EntityGraphRelationship(
                    observation.Source,
                    observation.Target,
                    observation.RelationshipType,
                    status,
                    preserveConfirmed ? current!.MatchType : observation.MatchType,
                    preserveConfirmed ? current!.Score : observation.Score,
                    preserveConfirmed ? current!.Evidence : observation.Evidence.ToArray(),
                    firstObservedAt,
                    observation.ObservedAt,
                    status == EntityGraphRelationshipStatuses.Confirmed
                        ? current?.ConfirmedAt ?? observation.ObservedAt
                        : current?.ConfirmedAt,
                    observation.PlanId ?? current?.LastPlanId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EntityGraphRecord>> QueryEntitiesAsync(
        EntityGraphQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCount(query.Count);
        var tenantId = EntityGraphPersistence.Require(query.TenantId, nameof(query.TenantId), 256);
        var search = query.Search?.Trim();
        lock (gate)
        {
            var result = records.Values
                .Where(record => record.Key.TenantId.Equals(tenantId, StringComparison.Ordinal))
                .Where(record => Matches(record.Key.Vendor, query.Vendor))
                .Where(record => Matches(record.Key.ConnectionId, query.ConnectionId))
                .Where(record => Matches(record.Key.EntityType, query.EntityType))
                .Where(record => !record.Entity.IsDeleted)
                .Where(record => string.IsNullOrWhiteSpace(search)
                    || record.Entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || record.Key.EntityId.Equals(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.LastObservedAt)
                .ThenBy(record => record.Entity.Name, StringComparer.OrdinalIgnoreCase)
                .Take(query.Count)
                .ToArray();
            return Task.FromResult<IReadOnlyList<EntityGraphRecord>>(result);
        }
    }

    public Task<IReadOnlyList<EntityGraphRelationship>> QueryRelationshipsAsync(
        EntityGraphRelationshipQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCount(query.Count);
        var nodeIdentity = EntityGraphPersistence.Key(query.Node);
        lock (gate)
        {
            var result = relationships.Values
                .Where(relationship =>
                    EntityGraphPersistence.Key(relationship.Source).Equals(nodeIdentity, StringComparison.Ordinal)
                    || EntityGraphPersistence.Key(relationship.Target).Equals(nodeIdentity, StringComparison.Ordinal))
                .Where(relationship => string.IsNullOrWhiteSpace(query.RelationshipType)
                    || relationship.RelationshipType.Equals(query.RelationshipType.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(relationship => relationship.LastObservedAt)
                .Take(query.Count)
                .ToArray();
            return Task.FromResult<IReadOnlyList<EntityGraphRelationship>>(result);
        }
    }

    public Task<EntityRefreshSnapshotResult> ReplaceAuthoritativeSnapshotAsync(
        EntityGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.ConnectionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                "Connection generation must be positive.");
        if (snapshot.SnapshotStartedAt > snapshot.ObservedAt)
            throw new ArgumentException(
                "Snapshot started-at must precede or equal observed-at.",
                nameof(snapshot));
        var scope = EntityGraphPersistence.ValidateScope(snapshot.Scope);
        var planId = EntityGraphPersistence.Optional(snapshot.PlanId, 128);
        // Prefer the source-supplied timestamp so the authoritative run records the
        // vendor's ordering. Falls back to the worker's observed-at for adapters
        // that don't ship a source cursor timestamp.
        var effectiveTimestamp = snapshot.SourceUpdatedAt ?? snapshot.ObservedAt;
        lock (gate)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var upserted = 0L;
            foreach (var entity in snapshot.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = EntityGraphPersistence.Serialize(entity);
                var hash = EntityGraphPersistence.Hash(payload);
                var key = EntityGraphPersistence.ValidateKey(new EntityGraphNodeKey(
                    scope.TenantId, scope.Vendor, scope.ConnectionId, scope.EntityType, entity.Id));
                var identity = EntityGraphPersistence.Key(key);
                seenIds.Add(identity);
                records.TryGetValue(identity, out var current);
                var firstObservedAt = current is null
                    ? effectiveTimestamp
                    : Earlier(current.FirstObservedAt, effectiveTimestamp);
                records[identity] = new EntityGraphRecord(
                    key,
                    EntityGraphPersistence.Deserialize(payload),
                    hash,
                    firstObservedAt,
                    effectiveTimestamp,
                    planId ?? current?.LastPlanId);
                upserted++;
            }

            var tombstoned = 0L;
            var preserved = 0L;
            foreach (var entry in records.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = entry.Value.Key;
                if (!key.TenantId.Equals(scope.TenantId, StringComparison.Ordinal)
                    || !key.Vendor.Equals(scope.Vendor, StringComparison.Ordinal)
                    || !key.ConnectionId.Equals(scope.ConnectionId, StringComparison.Ordinal)
                    || !key.EntityType.Equals(scope.EntityType, StringComparison.Ordinal))
                    continue;
                // Skip seen entities; they were just upserted by this snapshot.
                if (seenIds.Contains(EntityGraphPersistence.Key(key))) continue;
                if (entry.Value.LastObservedAt >= snapshot.SnapshotStartedAt)
                {
                    // A pre-existing record whose last observation falls inside the
                    // snapshot window was updated concurrently; the snapshot must
                    // not erase it.
                    preserved++;
                    continue;
                }
                if (entry.Value.FirstObservedAt >= snapshot.SnapshotStartedAt) continue;
                var tombstone = entry.Value.Entity;
                tombstone.IsActive = false;
                tombstone.IsDeleted = true;
                tombstone.CustomFields["EntitySyncRecordState"] = "TombstonedByFullSnapshot";
                records[EntityGraphPersistence.Key(key)] = entry.Value with
                {
                    Entity = tombstone,
                    LastObservedAt = effectiveTimestamp
                };
                tombstoned++;
            }

            return Task.FromResult(new EntityRefreshSnapshotResult(
                scope,
                snapshot.SnapshotStartedAt,
                snapshot.ObservedAt,
                upserted,
                tombstoned,
                preserved,
                upserted));
        }
    }

    public Task<EntityAtomicEventOutcome> ApplyAtomicEventAsync(
        EntityGraphScope scope,
        EntityAtomicEvent atomicEvent,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (atomicEvent.EventId == Guid.Empty)
            throw new ArgumentException("Atomic event ID is required.", nameof(atomicEvent));
        if (connectionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration),
                "Connection generation must be positive.");
        if (string.IsNullOrWhiteSpace(atomicEvent.EntityType))
            throw new ArgumentException("Atomic event entity type is required.",
                nameof(atomicEvent));
        if (atomicEvent.Operation == EntityAtomicOperation.Upsert && atomicEvent.Entity is null)
            throw new ArgumentException("Upsert events require an entity payload.", nameof(atomicEvent));
        if (atomicEvent.Operation == EntityAtomicOperation.Delete
            && (atomicEvent.Entity is null || string.IsNullOrWhiteSpace(atomicEvent.Entity.Id)))
            throw new ArgumentException("Delete events require the entity ID.", nameof(atomicEvent));
        var validatedScope = EntityGraphPersistence.ValidateScope(scope);
        var now = DateTimeOffset.UtcNow;
        var generationKey = $"{validatedScope.TenantId}|{validatedScope.ConnectionId}";
        lock (gate)
        {
            // Duplicate (idempotency) is consulted before the generation check so a
            // duplicate replay that was authored at the correct generation still
            // returns its stored outcome even after a subsequent rotate.
            if (appliedAtomicEvents.Contains(atomicEvent.EventId))
            {
                return Task.FromResult(new EntityAtomicEventOutcome(
                    EntityAtomicEventOutcomeKind.Duplicate, null, now));
            }
            if (connectionGenerations.TryGetValue(generationKey, out var knownGeneration)
                && knownGeneration != connectionGeneration)
            {
                throw new ConnectionGenerationConflictException(
                    validatedScope.ConnectionId, connectionGeneration);
            }
            connectionGenerations[generationKey] = connectionGeneration;
            appliedAtomicEvents.Add(atomicEvent.EventId);
            var entityId = atomicEvent.Entity!.Id;
            var key = EntityGraphPersistence.ValidateKey(new EntityGraphNodeKey(
                validatedScope.TenantId, validatedScope.Vendor, validatedScope.ConnectionId,
                atomicEvent.EntityType, entityId));
            var identity = EntityGraphPersistence.Key(key);
            if (atomicEvent.Operation == EntityAtomicOperation.Upsert)
            {
                // Use the event's source timestamp (when present) for ordering so the
                // graph consistently prefers vendor-supplied time over wall-clock.
                var effectiveTimestamp = atomicEvent.SourceUpdatedAt ?? now;
                var payload = EntityGraphPersistence.Serialize(atomicEvent.Entity);
                var hash = EntityGraphPersistence.Hash(payload);
                records.TryGetValue(identity, out var current);
                if (current is not null && effectiveTimestamp < current.LastObservedAt)
                {
                    // Late event with older timestamp: keep the existing record, but
                    // still return the receipt so callers see the atomic outcome.
                    return Task.FromResult(new EntityAtomicEventOutcome(
                        EntityAtomicEventOutcomeKind.Duplicate, current, current.LastObservedAt));
                }
                var firstObservedAt = current is null
                    ? effectiveTimestamp
                    : Earlier(current.FirstObservedAt, effectiveTimestamp);
                records[identity] = new EntityGraphRecord(
                    key,
                    EntityGraphPersistence.Deserialize(payload),
                    hash,
                    firstObservedAt,
                    effectiveTimestamp,
                    atomicEvent.EventId.ToString());
                return Task.FromResult(new EntityAtomicEventOutcome(
                    EntityAtomicEventOutcomeKind.Applied, records[identity], effectiveTimestamp));
            }
            if (!records.TryGetValue(identity, out var existing))
            {
                return Task.FromResult(new EntityAtomicEventOutcome(
                    EntityAtomicEventOutcomeKind.NotFound, null, now));
            }
            var tombstone = existing.Entity;
            tombstone.IsActive = false;
            tombstone.IsDeleted = true;
            tombstone.CustomFields["EntitySyncRecordState"] = "TombstonedByAtomicEvent";
            records[identity] = existing with
            {
                Entity = tombstone,
                LastObservedAt = now,
                LastPlanId = atomicEvent.EventId.ToString()
            };
            return Task.FromResult(new EntityAtomicEventOutcome(
                EntityAtomicEventOutcomeKind.Applied, records[identity], now));
        }
    }

    public Task<EntityAtomicEventOutcome?> TryGetAtomicEventReceiptAsync(
        string tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        lock (gate)
        {
            if (!appliedAtomicEvents.Contains(eventId))
                return Task.FromResult<EntityAtomicEventOutcome?>(null);
            EntityGraphRecord? match = null;
            foreach (var entry in records.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.LastPlanId is not null
                    && entry.LastPlanId.Equals(eventId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }
            return Task.FromResult<EntityAtomicEventOutcome?>(new EntityAtomicEventOutcome(
                EntityAtomicEventOutcomeKind.Duplicate, match, match?.LastObservedAt ?? DateTimeOffset.UtcNow));
        }
    }

    private static bool Matches(string actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 1000.");
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

}
