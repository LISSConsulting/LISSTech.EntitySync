using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityGraphRepository
{
    Task ObserveEntitiesAsync(EntityGraphObservation observation, CancellationToken cancellationToken);

    Task ObserveRelationshipsAsync(
        IReadOnlyCollection<EntityGraphRelationshipObservation> relationships,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityGraphRecord>> QueryEntitiesAsync(
        EntityGraphQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityGraphRelationship>> QueryRelationshipsAsync(
        EntityGraphRelationshipQuery query,
        CancellationToken cancellationToken);

    Task<EntityRefreshSnapshotResult> ReplaceAuthoritativeSnapshotAsync(
        EntityGraphSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<EntityAtomicEventOutcome> ApplyAtomicEventAsync(
        EntityGraphScope scope,
        EntityAtomicEvent atomicEvent,
        long connectionGeneration,
        CancellationToken cancellationToken);

    Task<EntityAtomicEventOutcome?> TryGetAtomicEventReceiptAsync(
        string tenantId,
        Guid eventId,
        CancellationToken cancellationToken);
}

public sealed record EntityGraphSnapshot(
    EntityGraphScope Scope,
    long ConnectionGeneration,
    IReadOnlyList<ExternalEntity> Entities,
    DateTimeOffset SnapshotStartedAt,
    DateTimeOffset ObservedAt,
    string? Cursor = null,
    DateTimeOffset? SourceUpdatedAt = null,
    string? PlanId = null);

public sealed record EntityRefreshSnapshotResult(
    EntityGraphScope Scope,
    DateTimeOffset SnapshotStartedAt,
    DateTimeOffset SnapshotCompletedAt,
    long UpsertedCount,
    long TombstonedCount,
    long PreservedAfterBoundaryCount,
    long ObservedCount);

public enum EntityAtomicEventOutcomeKind
{
    Applied,
    Duplicate,
    NotFound
}

public sealed record EntityAtomicEventOutcome(
    EntityAtomicEventOutcomeKind Kind,
    EntityGraphRecord? Record = null,
    DateTimeOffset AppliedAt = default);
