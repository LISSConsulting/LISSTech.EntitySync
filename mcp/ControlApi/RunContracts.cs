using System.ComponentModel.DataAnnotations;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record RunResponse(
    Guid RunId,
    Guid PlanId,
    Guid? ApprovalId,
    string RouteScope,
    string Mode,
    string Status,
    int Attempt,
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    int SkippedCount,
    int UnknownCount,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static RunResponse From(EntitySyncOperation value) => new(
        value.OperationId,
        value.PlanId,
        value.ApprovalId,
        value.RouteScope,
        value.Mode.ToString(),
        value.Status.ToString(),
        value.Attempt,
        value.TotalCount,
        value.SucceededCount,
        value.FailedCount,
        value.SkippedCount,
        value.UnknownCount,
        value.QueuedAt,
        value.StartedAt,
        value.CompletedAt);
}

public sealed record RunPageResponse(
    IReadOnlyList<RunResponse> Items,
    [property: Required] string ReplayCursor,
    string? NextCursor);

public sealed record RunItemResponse(
    Guid ItemId,
    Guid PlanId,
    string SourceVendor,
    string SourceEntityType,
    string SourceEntityId,
    string TargetVendor,
    string TargetEntityType,
    string? TargetEntityId,
    string Action,
    string Outcome,
    string? ErrorCode,
    string? SafeMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static RunItemResponse From(EntitySyncOperationItem value) => new(
        value.ItemId,
        value.PlanId,
        value.SourceVendor,
        value.SourceEntityType,
        value.SourceEntityId,
        value.TargetVendor,
        value.TargetEntityType,
        value.TargetEntityId,
        value.Action,
        value.Outcome.ToString(),
        value.ErrorCode,
        value.ErrorMessage,
        value.StartedAt,
        value.CompletedAt);
}

public sealed record QueuedRunResponse(
    Guid RunId,
    Guid PlanId,
    string Mode,
    string Status,
    string CorrelationId);
