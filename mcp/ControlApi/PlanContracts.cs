using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record CreatePlanRequest(
    Guid PolicyId,
    int? PolicyVersion,
    string? SourceSearch,
    int? SourceCount,
    string? SourceEntityId,
    int LifetimeMinutes = 60);

public sealed record InspectPlanRequest(
    string? Cursor,
    int PageSize = 25);

public sealed record ApprovePlanRequest(string Digest);

public sealed record ApplyPlanRequest(Guid ApprovalId);

public sealed record PlanResponse(
    Guid PlanId,
    Guid PolicyId,
    int PolicyVersion,
    string PolicyDefinitionSha256,
    string RouteScope,
    string SourceConnectionId,
    long SourceConnectionGeneration,
    string TargetConnectionId,
    long TargetConnectionGeneration,
    string Digest,
    string Status,
    int ItemCount,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ExpiresAt)
{
    public static PlanResponse From(EntitySyncDurablePlan value) => new(
        value.PlanId,
        value.PolicyId,
        value.PolicyVersion,
        value.PolicyDefinitionSha256.Value,
        value.RouteScope,
        value.SourceConnectionId,
        value.SourceConnectionGeneration,
        value.TargetConnectionId,
        value.TargetConnectionGeneration,
        value.PlanDigestSha256.Value,
        value.Status.ToString(),
        value.ItemCount,
        value.CreatedAt,
        value.CreatedBy.ActorId,
        value.ExpiresAt);
}

public sealed record PlanItemResponse(
    Guid ItemId,
    int Ordinal,
    string SourceVendor,
    string SourceConnectionId,
    string SourceEntityType,
    string SourceEntityKey,
    string SourceEntityId,
    string TargetVendor,
    string TargetConnectionId,
    string TargetEntityType,
    string? TargetEntityId,
    string Action,
    int MatchScore,
    string MatchType,
    IReadOnlyList<string> MatchReasons,
    string RedactedBeforeJson,
    string RedactedDesiredJson,
    string? BeforePayloadSha256,
    string DesiredPayloadSha256)
{
    public static PlanItemResponse From(EntitySyncDurablePlanItem value) => new(
        value.ItemId,
        value.ItemOrdinal,
        value.SourceVendor,
        value.SourceConnectionId,
        value.SourceEntityType,
        value.SourceEntityKey,
        value.SourceEntityId,
        value.TargetVendor,
        value.TargetConnectionId,
        value.TargetEntityType,
        value.TargetEntityId,
        value.Action,
        value.MatchEvidence.Score,
        value.MatchEvidence.MatchType,
        value.MatchEvidence.Reasons,
        value.RedactedBefore.Json,
        value.RedactedDesired.Json,
        value.BeforePayloadSha256?.Value,
        value.DesiredPayloadSha256.Value);
}

public sealed record InspectionResponse(
    Guid PlanId,
    Guid InspectionId,
    string Digest,
    int InspectedItems,
    bool Complete,
    IReadOnlyList<PlanItemResponse> Items,
    string? NextCursor);

public sealed record ApprovalResponse(
    Guid PlanId,
    Guid ApprovalId,
    Guid InspectionId,
    string Digest,
    DateTimeOffset ApprovedAt,
    DateTimeOffset? ExpiresAt);
