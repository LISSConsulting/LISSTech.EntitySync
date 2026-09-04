using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record EntityRefreshResponse(
    string ConnectionId,
    long ConnectionGeneration,
    string EntityType,
    string Status,
    string Mode,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset NextScheduledAt,
    long ObservedCount,
    string? ErrorCode,
    string? Cursor,
    DateTimeOffset? SourceUpdatedAt,
    bool IsStale)
{
    public static EntityRefreshResponse From(EntityRefreshStateSnapshot state) =>
        new(
            state.Key.ConnectionId,
            state.ConnectionGeneration,
            state.Key.EntityType,
            state.Status.ToString(),
            state.Mode.ToString(),
            state.LastAttemptAt,
            state.LastSuccessfulAt,
            state.NextScheduledAt,
            state.ObservedCount,
            state.ErrorCode,
            state.Cursor,
            state.SourceUpdatedAt,
            state.IsStale);
}

public sealed record EntityRefreshListResponse(
    IReadOnlyList<EntityRefreshResponse> Items);

public sealed record QueueEntityRefreshRequest(
    long ExpectedGeneration,
    string? EntityType = null);

public sealed record AtomicEntityEventRequest(
    long ExpectedGeneration,
    Guid EventId,
    string EntityType,
    EntityAtomicOperation Operation,
    ExternalEntityPayload? Entity = null,
    string? SourceCursor = null,
    DateTimeOffset? SourceUpdatedAt = null);

public sealed record ExternalEntityPayload
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? ParentId { get; set; }
    public string? ParentEntityType { get; set; }
    public long? Version { get; set; }
    public string? LifecycleStatus { get; set; }
    public bool? IsActive { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AtomicEntityEventResponse(
    Guid EventId,
    string ConnectionId,
    string EntityType,
    string EntityId,
    string Operation,
    string Outcome,
    DateTimeOffset AppliedAt,
    string? PayloadSha256);
