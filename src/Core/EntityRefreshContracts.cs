namespace LISSTech.EntitySync.Core;

public enum EntityRefreshStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public enum EntityRefreshMode
{
    Scheduled,
    Manual,
    Incremental
}

public enum EntityRefreshEventOperation
{
    QueueSnapshot,
    SnapshotStarted,
    SnapshotCompleted,
    SnapshotFailed,
    AtomicUpsert,
    AtomicDelete
}

public enum EntityAtomicOperation
{
    Upsert,
    Delete
}

public sealed record EntityRefreshStateKey(
    string TenantId,
    string ConnectionId,
    string EntityType);

public sealed record EntityRefreshStateSnapshot
{
    public required EntityRefreshStateKey Key { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public long ConnectionGeneration { get; init; }
    public EntityRefreshStatus Status { get; init; }
    public EntityRefreshMode Mode { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessfulAt { get; init; }
    public DateTimeOffset NextScheduledAt { get; init; }
    public long ObservedCount { get; init; }
    public string? Cursor { get; init; }
    public DateTimeOffset? SourceUpdatedAt { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset? SnapshotStartedAt { get; init; }
    public DateTimeOffset? SnapshotCompletedAt { get; init; }
    public bool IsStale { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public EntityRefreshStateSnapshot With(
        EntityRefreshStatus? status = null,
        EntityRefreshMode? mode = null,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? lastSuccessfulAt = null,
        DateTimeOffset? nextScheduledAt = null,
        long? observedCount = null,
        string? cursor = null,
        DateTimeOffset? sourceUpdatedAt = null,
        string? errorCode = null,
        DateTimeOffset? snapshotStartedAt = null,
        DateTimeOffset? snapshotCompletedAt = null,
        bool? isStale = null,
        string? leaseOwner = null,
        DateTimeOffset? leaseExpiresAt = null) =>
        new()
        {
            Key = Key,
            Vendor = Vendor,
            ConnectionGeneration = ConnectionGeneration,
            Status = status ?? Status,
            Mode = mode ?? Mode,
            LastAttemptAt = lastAttemptAt ?? LastAttemptAt,
            LastSuccessfulAt = lastSuccessfulAt ?? LastSuccessfulAt,
            NextScheduledAt = nextScheduledAt ?? NextScheduledAt,
            ObservedCount = observedCount ?? ObservedCount,
            Cursor = cursor ?? Cursor,
            SourceUpdatedAt = sourceUpdatedAt ?? SourceUpdatedAt,
            ErrorCode = errorCode ?? ErrorCode,
            SnapshotStartedAt = snapshotStartedAt ?? SnapshotStartedAt,
            SnapshotCompletedAt = snapshotCompletedAt ?? SnapshotCompletedAt,
            IsStale = isStale ?? IsStale,
            LeaseOwner = leaseOwner ?? LeaseOwner,
            LeaseExpiresAt = leaseExpiresAt ?? LeaseExpiresAt
        };
}

public sealed record EntityRefreshEvent(
    Guid EventId,
    EntityRefreshStateKey Key,
    string Vendor,
    EntityRefreshMode Mode,
    EntityRefreshEventOperation Operation,
    EntityRefreshStatus Status,
    DateTimeOffset? SnapshotStartedAt,
    DateTimeOffset? SnapshotCompletedAt,
    long? ObservedCount,
    string? SourceCursor,
    DateTimeOffset? SourceUpdatedAt,
    string? ErrorCode,
    DateTimeOffset ReceivedAt);

public sealed record EntityAtomicEventReceipt(
    Guid EventId,
    string ConnectionId,
    string EntityType,
    string EntityId,
    EntityAtomicOperation Operation,
    EntitySyncSha256 PayloadSha256,
    string? SourceCursor,
    DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset AppliedAt);

public sealed record EntityAtomicEvent(
    Guid EventId,
    string ConnectionId,
    string EntityType,
    EntityAtomicOperation Operation,
    ExternalEntity? Entity,
    string? SourceCursor,
    DateTimeOffset? SourceUpdatedAt);

public sealed record EntityRefreshCapability(
    string TenantId,
    string ConnectionId,
    string Vendor,
    string EntityType,
    bool SupportsRefresh,
    DateTimeOffset LastDiscoveredAt);

public static class EntityRefreshConstants
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(60);
    public const string DefaultErrorNone = "NONE";
    public const string ErrorConnectionUnavailable = "CONNECTION_UNAVAILABLE";
    public const string ErrorDependencyUnavailable = "DEPENDENCY_UNAVAILABLE";
    public const string ErrorAdapterThrew = "ADAPTER_THREW";
    public const string ErrorGenerationConflict = "GENERATION_CONFLICT";
    public const string ErrorUnknownEntityType = "UNKNOWN_ENTITY_TYPE";
}
