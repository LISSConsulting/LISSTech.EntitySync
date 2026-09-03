using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntityGraphRepository : IEntityGraphRepository
{
    private readonly object gate = new();
    private readonly Dictionary<string, EntityGraphRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGraphRelationship> relationships = new(StringComparer.Ordinal);

    public Task ObserveEntitiesAsync(EntityGraphObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = EntityGraphPersistence.ValidateScope(observation.Scope);
        var planId = EntityGraphPersistence.Optional(observation.PlanId, 128);
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
                    ? observation.ObservedAt
                    : Earlier(current.FirstObservedAt, observation.ObservedAt);
                if (current is not null && observation.ObservedAt < current.LastObservedAt)
                {
                    records[identity] = current with { FirstObservedAt = firstObservedAt };
                    continue;
                }
                records[identity] = new EntityGraphRecord(
                    key,
                    snapshot,
                    hash,
                    firstObservedAt,
                    observation.ObservedAt,
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

    private static bool Matches(string actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 1000.");
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}
