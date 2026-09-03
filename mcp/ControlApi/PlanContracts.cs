using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

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
        var remainingEntities = CanonicalShadowEntityRequest.MaximumGraphEntities;
        return ToDomain(ref remainingEntities);
    }

    internal CanonicalEntityVersion ToDomain(ref int remainingEntities)
    {
        if (CanonicalEntityId == Guid.Empty || CanonicalVersion <= 0)
            throw new ArgumentException("Canonical shadow identity and version are required.");
        ArgumentNullException.ThrowIfNull(Entity);
        return new CanonicalEntityVersion(
            CanonicalEntityId,
            CanonicalVersion,
            Entity.ToDomain(
                CanonicalEntityId,
                CanonicalVersion,
                depth: 0,
                ref remainingEntities,
                isRoot: true));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalShadowEntityRequest(
    [property: Required] string EntityType,
    [property: Required] string Id,
    string? ParentId,
    string? ParentEntityType,
    long? Version,
    string? LifecycleStatus,
    bool IsDeleted,
    string? MergeSurvivorId,
    [property: Required] IReadOnlyList<string> MergeDonorIds,
    [property: Required] IReadOnlyList<string> Tags,
    [property: Required] IReadOnlyList<CanonicalShadowEntityRequest> Children,
    [property: Required] IReadOnlyList<CanonicalShadowPlatformLinkRequest> PlatformLinks,
    [property: Required] IReadOnlyDictionary<string, string> ExternalIds,
    [property: Required] string Name,
    string? Email,
    string? Phone,
    string? Website,
    string? Domain,
    string? PrimarySiteId,
    string? PrimarySiteName,
    CanonicalShadowAddressRequest? PrimaryAddress,
    CanonicalShadowAddressRequest? BillingAddress,
    CanonicalShadowAddressRequest? ShippingAddress,
    bool? IsActive,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    [property: Required] IReadOnlyDictionary<string, string?> CustomFields)
{
    internal const int MaximumGraphEntities = 5000;
    private const int MaximumGraphDepth = 2;

    public ExternalEntity ToDomain(Guid canonicalEntityId, long canonicalVersion)
    {
        var remainingEntities = MaximumGraphEntities;
        return ToDomain(
            canonicalEntityId,
            canonicalVersion,
            depth: 0,
            ref remainingEntities,
            isRoot: true);
    }

    internal ExternalEntity ToDomain(
        Guid canonicalEntityId,
        long canonicalVersion,
        int depth,
        ref int remainingEntities,
        bool isRoot)
    {
        if (--remainingEntities < 0)
            throw new ArgumentException("Canonical shadow entity graph is too large.");
        if (depth > MaximumGraphDepth)
            throw new ArgumentException("Canonical shadow entity graph is too deep.");

        var id = CanonicalId(Id, nameof(Id));
        if (isRoot
            && (id != canonicalEntityId.ToString("D")
                || Version != canonicalVersion))
            throw new ArgumentException(
                "Canonical shadow entity identity or version does not match.");
        if (Version is null or <= 0)
            throw new ArgumentException("Canonical shadow entity version is required.");
        if ((ParentId is null) != (ParentEntityType is null))
            throw new ArgumentException(
                "Canonical shadow parent identity and type must be supplied together.");

        ArgumentNullException.ThrowIfNull(Children);
        if (Children.Count > 1000)
            throw new ArgumentException("Canonical shadow children are invalid.");
        if (depth == MaximumGraphDepth && Children.Count > 0)
            throw new ArgumentException("Canonical shadow entity graph is too deep.");

        var children = new List<ExternalEntity>(Children.Count);
        foreach (var child in Children)
        {
            ArgumentNullException.ThrowIfNull(child);
            children.Add(child.ToDomain(
                canonicalEntityId,
                canonicalVersion,
                depth + 1,
                ref remainingEntities,
                isRoot: false));
        }

        return new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = Required(EntityType, 100, nameof(EntityType)),
            Id = id,
            ParentId = ParentId is null ? null : CanonicalId(ParentId, nameof(ParentId)),
            ParentEntityType = Optional(ParentEntityType, 100, nameof(ParentEntityType)),
            Version = Version,
            LifecycleStatus = Optional(LifecycleStatus, 100, nameof(LifecycleStatus)),
            IsDeleted = IsDeleted,
            MergeSurvivorId = MergeSurvivorId is null
                ? null
                : CanonicalId(MergeSurvivorId, nameof(MergeSurvivorId)),
            MergeDonorIds = CopyCanonicalIds(MergeDonorIds, 100, nameof(MergeDonorIds)),
            Tags = CopyStrings(Tags, 100, 200, nameof(Tags)),
            Children = children,
            PlatformLinks = CopyPlatformLinks(PlatformLinks),
            ExternalIds = CopyExternalIds(ExternalIds),
            Name = Required(Name, 500, nameof(Name)),
            Email = Optional(Email, 500, nameof(Email)),
            Phone = Optional(Phone, 100, nameof(Phone)),
            Website = Optional(Website, 500, nameof(Website)),
            Domain = Optional(Domain, 500, nameof(Domain)),
            PrimarySiteId = PrimarySiteId is null
                ? null
                : CanonicalId(PrimarySiteId, nameof(PrimarySiteId)),
            PrimarySiteName = Optional(PrimarySiteName, 500, nameof(PrimarySiteName)),
            PrimaryAddress = PrimaryAddress?.ToDomain(),
            BillingAddress = BillingAddress?.ToDomain(),
            ShippingAddress = ShippingAddress?.ToDomain(),
            IsActive = IsActive,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            CustomFields = CopyCustomFields(CustomFields)
        };
    }

    private static List<ExternalPlatformLink> CopyPlatformLinks(
        IReadOnlyList<CanonicalShadowPlatformLinkRequest> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 100)
            throw new ArgumentException("Canonical platform links are invalid.");
        return values.Select(value =>
        {
            ArgumentNullException.ThrowIfNull(value);
            return value.ToDomain();
        }).ToList();
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
            pair => Optional(pair.Value, 4000, nameof(values)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> CopyCanonicalIds(
        IReadOnlyList<string> values,
        int maximumCount,
        string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumCount)
            throw new ArgumentException($"{name} is invalid.", name);
        var result = values.Select(value => CanonicalId(value, name)).ToList();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ArgumentException($"{name} is invalid.", name);
        return result;
    }

    private static List<string> CopyStrings(
        IReadOnlyList<string> values,
        int maximumCount,
        int maximumLength,
        string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumCount)
            throw new ArgumentException($"{name} is invalid.", name);
        return values.Select(value => Required(value, maximumLength, name)).ToList();
    }

    private static string CanonicalId(string value, string name)
    {
        var candidate = Required(value, 36, name);
        if (!Guid.TryParseExact(candidate, "D", out var parsed)
            || parsed == Guid.Empty
            || parsed.ToString("D") != candidate)
            throw new ArgumentException($"{name} is invalid.", name);
        return candidate;
    }

    internal static string Required(string value, int maximum, string name) =>
        Optional(value, maximum, name)
        ?? throw new ArgumentException($"{name} is required.", name);

    internal static string? Optional(string? value, int maximum, string name)
    {
        if (value is null) return null;
        if (value.Length == 0 || value.Length > maximum || value != value.Trim()
            || value.Any(character => char.IsControl(character)))
            throw new ArgumentException($"{name} is invalid.", name);
        return value;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalShadowPlatformLinkRequest(
    [property: Required] string PlatformInstanceId,
    [property: Required] string Platform,
    [property: Required] string ExternalId,
    [property: Required] string Status,
    [property: Required] string EntityType,
    [property: Required] string EntityId)
{
    internal ExternalPlatformLink ToDomain() => new()
    {
        PlatformInstanceId = CanonicalShadowEntityRequest.Required(
            PlatformInstanceId, 200, nameof(PlatformInstanceId)),
        Platform = CanonicalShadowEntityRequest.Required(
            Platform, 100, nameof(Platform)),
        ExternalId = CanonicalShadowEntityRequest.Required(
            ExternalId, 500, nameof(ExternalId)),
        Status = CanonicalShadowEntityRequest.Required(Status, 100, nameof(Status)),
        EntityType = CanonicalShadowEntityRequest.Required(
            EntityType, 100, nameof(EntityType)),
        EntityId = CanonicalEntityId(EntityId)
    };

    private static string CanonicalEntityId(string value)
    {
        var candidate = CanonicalShadowEntityRequest.Required(
            value, 36, nameof(EntityId));
        if (!Guid.TryParseExact(candidate, "D", out var parsed)
            || parsed == Guid.Empty
            || parsed.ToString("D") != candidate)
            throw new ArgumentException("Platform link entity ID is invalid.");
        return candidate;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalShadowAddressRequest(
    string? AddressType,
    string? Attention,
    string? Line1,
    string? Line2,
    string? Line3,
    string? City,
    string? State,
    string? PostalCode,
    string? Country)
{
    internal EntityAddress ToDomain() => new()
    {
        AddressType = Value(AddressType, nameof(AddressType)),
        Attention = Value(Attention, nameof(Attention)),
        Line1 = Value(Line1, nameof(Line1)),
        Line2 = Value(Line2, nameof(Line2)),
        Line3 = Value(Line3, nameof(Line3)),
        City = Value(City, nameof(City)),
        State = Value(State, nameof(State)),
        PostalCode = Value(PostalCode, nameof(PostalCode)),
        Country = Value(Country, nameof(Country))
    };

    private static string? Value(string? value, string name) =>
        CanonicalShadowEntityRequest.Optional(value, 500, name);
}

internal sealed class CanonicalShadowEntitySchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(CanonicalShadowEntityRequest)
            || !schema.Properties.TryGetValue("customFields", out var customFields)
            || customFields.AdditionalProperties is null)
            return;
        customFields.AdditionalProperties.Nullable = true;
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
