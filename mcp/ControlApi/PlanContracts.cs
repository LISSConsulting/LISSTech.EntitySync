using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record CreatePlanRequest(
    Guid PolicyId,
    int? PolicyVersion,
    string? SourceSearch,
    int? SourceCount,
    string? SourceEntityId,
    int LifetimeMinutes = 60);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateShadowPlanRequest(
    Guid PolicyId,
    int PolicyVersion,
    [property: Required] IReadOnlyList<CanonicalShadowSourceRequest> Sources,
    int LifetimeMinutes = 60);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalShadowSourceRequest(
    Guid CanonicalEntityId,
    long CanonicalVersion,
    [property: Required] CanonicalShadowEntityRequest Entity)
{
    public CanonicalEntityVersion ToDomain()
    {
        if (CanonicalEntityId == Guid.Empty || CanonicalVersion <= 0)
            throw new ArgumentException("Canonical shadow identity and version are required.");
        ArgumentNullException.ThrowIfNull(Entity);
        return new CanonicalEntityVersion(
            CanonicalEntityId,
            CanonicalVersion,
            Entity.ToDomain(CanonicalEntityId, CanonicalVersion));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalShadowEntityRequest(
    [property: Required] string EntityType,
    [property: Required] string Id,
    [property: Required] string Name,
    string? Email,
    string? Phone,
    string? Website,
    string? Domain,
    bool? IsActive,
    [property: Required] IReadOnlyDictionary<string, string> ExternalIds,
    [property: Required] IReadOnlyDictionary<string, string?> CustomFields)
{
    public ExternalEntity ToDomain(Guid canonicalEntityId, long canonicalVersion)
    {
        var exactId = canonicalEntityId.ToString("D");
        if (Id != exactId)
            throw new ArgumentException("Canonical shadow entity identity does not match.");
        var entityType = Required(EntityType, 100, nameof(EntityType));
        var name = Required(Name, 500, nameof(Name));
        return new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = entityType,
            Id = exactId,
            Version = canonicalVersion,
            Name = name,
            Email = Optional(Email, 500, nameof(Email)),
            Phone = Optional(Phone, 100, nameof(Phone)),
            Website = Optional(Website, 500, nameof(Website)),
            Domain = Optional(Domain, 500, nameof(Domain)),
            IsActive = IsActive,
            ExternalIds = CopyExternalIds(ExternalIds),
            CustomFields = CopyCustomFields(CustomFields)
        };
    }

    private static Dictionary<string, string> CopyExternalIds(
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 100)
            throw new ArgumentException("Canonical external IDs are invalid.");
        return values.ToDictionary(
            pair => Required(pair.Key, 100, nameof(values)),
            pair => Required(pair.Value, 500, nameof(values)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> CopyCustomFields(
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 100)
            throw new ArgumentException("Canonical custom fields are invalid.");
        return values.ToDictionary(
            pair => Required(pair.Key, 100, nameof(values)),
            pair => Optional(pair.Value, 500, nameof(values)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string Required(string value, int maximum, string name) =>
        Optional(value, maximum, name)
        ?? throw new ArgumentException($"{name} is required.", name);

    private static string? Optional(string? value, int maximum, string name)
    {
        if (value is null) return null;
        if (value.Length == 0 || value.Length > maximum || value != value.Trim()
            || value.Any(character => char.IsControl(character)))
            throw new ArgumentException($"{name} is invalid.", name);
        return value;
    }
}

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
