namespace LISSTech.EntitySync.Core;

public sealed record EntityGraphScope(
    string TenantId,
    string Vendor,
    string ConnectionId,
    string EntityType);

public sealed record EntityGraphNodeKey(
    string TenantId,
    string Vendor,
    string ConnectionId,
    string EntityType,
    string EntityId);

public sealed record EntityGraphObservation(
    EntityGraphScope Scope,
    IReadOnlyCollection<ExternalEntity> Entities,
    DateTimeOffset ObservedAt,
    string? PlanId = null,
    string? Cursor = null,
    DateTimeOffset? SourceUpdatedAt = null);
public sealed record EntityGraphRelationshipObservation(
    EntityGraphNodeKey Source,
    EntityGraphNodeKey Target,
    string RelationshipType,
    string Status,
    string MatchType,
    int Score,
    IReadOnlyList<string> Evidence,
    DateTimeOffset ObservedAt,
    string? PlanId = null);

public sealed record EntityGraphRecord(
    EntityGraphNodeKey Key,
    ExternalEntity Entity,
    string PayloadHash,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string? LastPlanId);

public sealed record EntityGraphRelationship(
    EntityGraphNodeKey Source,
    EntityGraphNodeKey Target,
    string RelationshipType,
    string Status,
    string MatchType,
    int Score,
    IReadOnlyList<string> Evidence,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? ConfirmedAt,
    string? LastPlanId);

public sealed record EntityGraphQuery(
    string TenantId,
    string? Vendor = null,
    string? ConnectionId = null,
    string? EntityType = null,
    string? Search = null,
    bool IncludeInactive = false,
    int Offset = 0,
    int Count = 100);
public sealed record EntityGraphRelationshipQuery(
    EntityGraphNodeKey Node,
    string? RelationshipType = null,
    int Count = 100);

public static class EntityGraphRelationshipTypes
{
    public const string EquivalentTo = "EquivalentTo";
}

public static class EntityGraphRelationshipStatuses
{
    public const string Proposed = "Proposed";
    public const string Confirmed = "Confirmed";
    public const string Removed = "Removed";
}
