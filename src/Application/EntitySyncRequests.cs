using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public sealed class CreateEntitySyncPlanRequest
{
    public string TenantId { get; init; } = string.Empty;
    public string SourceVendor { get; init; } = string.Empty;
    public string? SourceConnectionId { get; init; }
    public string TargetVendor { get; init; } = string.Empty;
    public string? TargetConnectionId { get; init; }
    public string? SourceEntityType { get; init; }
    public string? SourceSearch { get; init; }
    public int? SourceCount { get; init; }
    public string? SourceEntityId { get; init; }
    internal CanonicalEntityVersion? PinnedCanonicalSource { get; init; }
    public string? TargetEntityType { get; init; }
    public bool CreateMissing { get; init; }
    public bool IncludeInactive { get; init; }
    public int AutoLinkScore { get; init; } = 90;
    public int ReviewScore { get; init; } = 70;
    public string? SourceExternalIdName { get; init; }
    public string? TargetCustomFieldName { get; init; }
    public EntitySyncUpdatePolicy UpdatePolicy { get; init; } = EntitySyncUpdatePolicy.Standard;
    public string? ChangeStateScope { get; init; }
}

public sealed class CreateDurablePlanRequest
{
    public string TenantId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public Guid PolicyId { get; init; }
    public int? PolicyVersion { get; init; }
    public string? SourceSearch { get; init; }
    public int? SourceCount { get; init; }
    public string? SourceEntityId { get; init; }
    internal CanonicalEntityVersion? PinnedCanonicalSource { get; init; }
    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromHours(4);
}

public sealed record DurablePlanResult(
    string TenantId,
    Guid PlanId,
    string Digest,
    int ItemCount,
    Guid PolicyId,
    int PolicyVersion,
    long SourceConnectionGeneration,
    long TargetConnectionGeneration,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public int PageCount(int pageSize)
    {
        if (pageSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        return ItemCount == 0 ? 0 : checked((ItemCount + pageSize - 1) / pageSize);
    }
}

public sealed record DurableInspectionRange(int StartInclusive, int EndExclusive);

public sealed record DurablePlanInspectionPage(
    DurablePlanResult Plan,
    int Page,
    int PageSize,
    IReadOnlyList<EntitySyncDurablePlanItem> Items,
    Guid InspectionId,
    IReadOnlyList<DurableInspectionRange> Coverage,
    int CoveredItemCount,
    bool InspectionComplete);

public sealed record DurablePlanApprovalResult(
    string TenantId,
    Guid PlanId,
    Guid ApprovalId,
    Guid InspectionId,
    string Digest,
    DateTimeOffset ApprovedAt,
    DateTimeOffset? ExpiresAt);

public sealed record EntitySyncPlanPage(
    string PlanId,
    string Status,
    string Digest,
    int Page,
    int PageSize,
    int TotalItems,
    IReadOnlyList<EntitySyncPlanItemView> Items);

public sealed record EntitySyncPlanItemView(
    int Index,
    string Action,
    string MatchType,
    int Score,
    string SourceId,
    string Source,
    string? TargetId,
    string? Target,
    IReadOnlyList<string> Reasons);

public sealed record EntitySyncApplyItemResult(
    string Action,
    string Source,
    string? Target,
    bool Success,
    bool Skipped,
    string? Id,
    string Message);

public sealed record EntitySyncApplyResult(
    string PlanId,
    bool Applied,
    bool Success,
    int Succeeded,
    int Failed,
    int Skipped,
    IReadOnlyList<EntitySyncApplyItemResult> Results);
