using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record PolicyDefinitionContract(
    string SourceVendor,
    string SourceConnectionId,
    string SourceEntityType,
    string TargetVendor,
    string TargetConnectionId,
    string TargetEntityType,
    bool IncludeInactive,
    bool CreateMissing,
    int AutoLinkScore,
    int ReviewScore,
    string? SourceExternalIdName,
    string? TargetCustomFieldName,
    EntitySyncUpdatePolicy UpdatePolicy,
    IReadOnlyList<string> AllowedFields,
    IReadOnlyList<string> BlockedFields,
    bool ScheduledApplySafeSubset)
{
    public EntitySyncPolicyDefinition ToDomain() => new(
        SourceVendor,
        SourceConnectionId,
        SourceEntityType,
        TargetVendor,
        TargetConnectionId,
        TargetEntityType,
        IncludeInactive,
        CreateMissing,
        AutoLinkScore,
        ReviewScore,
        SourceExternalIdName,
        TargetCustomFieldName,
        UpdatePolicy,
        AllowedFields,
        BlockedFields,
        ScheduledApplySafeSubset);

    public static PolicyDefinitionContract From(EntitySyncPolicyDefinition value) => new(
        value.SourceVendor,
        value.SourceConnectionId,
        value.SourceEntityType,
        value.TargetVendor,
        value.TargetConnectionId,
        value.TargetEntityType,
        value.IncludeInactive,
        value.CreateMissing,
        value.AutoLinkScore,
        value.ReviewScore,
        value.SourceExternalIdName,
        value.TargetCustomFieldName,
        value.UpdatePolicy,
        value.AllowedFields.Order(StringComparer.Ordinal).ToArray(),
        value.BlockedFields.Order(StringComparer.Ordinal).ToArray(),
        value.ScheduledApplySafeSubset);
}

public sealed record CreatePolicyRequest(
    string Name,
    string RouteScope,
    PolicyDefinitionContract Definition,
    bool Enabled);

public sealed record CreatePolicyVersionRequest(
    int ExpectedVersion,
    PolicyDefinitionContract Definition,
    bool? Enabled);

public sealed record PolicyResponse(
    Guid PolicyId,
    int Version,
    string Name,
    string RouteScope,
    PolicyDefinitionContract Definition,
    string DefinitionSha256,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static PolicyResponse From(EntitySyncPolicy value) => new(
        value.PolicyId,
        value.Version,
        value.Name,
        value.RouteScope,
        PolicyDefinitionContract.From(value.Definition),
        value.DefinitionSha256.Value,
        value.Enabled,
        value.CreatedAt,
        value.CreatedBy.ActorId);
}

public sealed record ExclusionRouteContract(
    string SourceVendor,
    string? SourceConnectionId,
    string? SourceEntityType,
    string TargetVendor,
    string? TargetConnectionId,
    string? TargetEntityType);

public sealed record CreateExclusionRequest(
    ExclusionRouteContract Route,
    string SourceEntityId,
    string SourceName,
    string Reason);

public sealed record DeleteExclusionRequest(
    ExclusionRouteContract Route,
    string SourceEntityId);

public sealed record ExclusionResponse(
    Guid ExclusionId,
    ExclusionRouteContract Route,
    string SourceEntityId,
    string SourceName,
    string Reason,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static ExclusionResponse From(EntityExclusion value) => new(
        value.Id,
        new ExclusionRouteContract(
            value.Route.SourceVendor,
            value.Route.SourceConnectionId,
            value.Route.SourceEntityType,
            value.Route.TargetVendor,
            value.Route.TargetConnectionId,
            value.Route.TargetEntityType),
        value.SourceEntityId,
        value.SourceName,
        value.Reason,
        value.CreatedBy,
        value.CreatedAt);
}

public sealed record CanonicalChangeIntakeRequest(
    string OutboxEventId,
    string CanonicalEntityType,
    Guid CanonicalEntityId,
    long CanonicalVersion,
    IReadOnlyList<string> ChangedFields,
    string PayloadSha256,
    DateTimeOffset OccurredAt);

public sealed record CanonicalChangeIntakeResponse(
    Guid ReceiptId,
    string OutboxEventId,
    Guid CanonicalEntityId,
    long CanonicalVersion,
    string PayloadSha256,
    IReadOnlyList<Guid> WorkIds,
    DateTimeOffset ReceivedAt,
    string CorrelationId);

public sealed record ControlPage<T>(IReadOnlyList<T> Items, string? NextCursor);
