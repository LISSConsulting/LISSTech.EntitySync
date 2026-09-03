using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LISSTech.EntitySync.Core;

public sealed record EntitySyncPolicyDefinition
{
    public EntitySyncPolicyDefinition(
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType,
        bool includeInactive,
        bool createMissing,
        int autoLinkScore,
        int reviewScore,
        string? sourceExternalIdName,
        string? targetCustomFieldName,
        EntitySyncUpdatePolicy updatePolicy,
        IEnumerable<string> allowedFields,
        IEnumerable<string> blockedFields,
        bool scheduledApplySafeSubset)
    {
        SourceVendor = EntitySyncVendors.Normalize(ControlModelGuard.Required(sourceVendor, nameof(sourceVendor)));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceEntityType = ControlModelGuard.Required(sourceEntityType, nameof(sourceEntityType));
        TargetVendor = EntitySyncVendors.Normalize(ControlModelGuard.Required(targetVendor, nameof(targetVendor)));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetEntityType = ControlModelGuard.Required(targetEntityType, nameof(targetEntityType));
        IncludeInactive = includeInactive;
        CreateMissing = createMissing;
        if (autoLinkScore is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(autoLinkScore), autoLinkScore, "Auto-link score must be between 0 and 100.");
        if (reviewScore is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(reviewScore), reviewScore, "Review score must be between 0 and 100.");
        if (reviewScore > autoLinkScore)
            throw new ArgumentException("Review score cannot exceed auto-link score.", nameof(reviewScore));
        AutoLinkScore = autoLinkScore;
        ReviewScore = reviewScore;
        SourceExternalIdName = ControlModelGuard.Optional(sourceExternalIdName, nameof(sourceExternalIdName));
        TargetCustomFieldName = ControlModelGuard.Optional(targetCustomFieldName, nameof(targetCustomFieldName));
        UpdatePolicy = ControlModelGuard.Defined(updatePolicy, nameof(updatePolicy));
        AllowedFields = ControlModelGuard.StringSet(allowedFields, nameof(allowedFields));
        BlockedFields = ControlModelGuard.StringSet(blockedFields, nameof(blockedFields));
        if (AllowedFields.Overlaps(BlockedFields))
            throw new ArgumentException("Allowed and blocked fields must not overlap.", nameof(blockedFields));
        ScheduledApplySafeSubset = scheduledApplySafeSubset;
    }

    public string SourceVendor { get; }
    public string SourceConnectionId { get; }
    public string SourceEntityType { get; }
    public string TargetVendor { get; }
    public string TargetConnectionId { get; }
    public string TargetEntityType { get; }
    public bool IncludeInactive { get; }
    public bool CreateMissing { get; }
    public int AutoLinkScore { get; }
    public int ReviewScore { get; }
    public string? SourceExternalIdName { get; }
    public string? TargetCustomFieldName { get; }
    public EntitySyncUpdatePolicy UpdatePolicy { get; }
    public IReadOnlySet<string> AllowedFields { get; }
    public IReadOnlySet<string> BlockedFields { get; }
    public bool ScheduledApplySafeSubset { get; }

    internal string ToCanonicalJson() => JsonSerializer.Serialize(new
    {
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
        AllowedFields = AllowedFields.Order(StringComparer.OrdinalIgnoreCase).ThenBy(value => value, StringComparer.Ordinal),
        BlockedFields = BlockedFields.Order(StringComparer.OrdinalIgnoreCase).ThenBy(value => value, StringComparer.Ordinal),
        ScheduledApplySafeSubset
    });
}

public sealed record EntitySyncPolicy
{
    public EntitySyncPolicy(
        string tenantId,
        Guid policyId,
        int version,
        string name,
        string routeScope,
        EntitySyncPolicyDefinition definition,
        EntitySyncSha256 definitionSha256,
        bool enabled,
        DateTimeOffset createdAt,
        EntitySyncActor createdBy)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        PolicyId = ControlModelGuard.NonEmpty(policyId, nameof(policyId));
        Version = ControlModelGuard.Positive(version, nameof(version));
        Name = ControlModelGuard.Required(name, nameof(name));
        RouteScope = ControlModelGuard.Required(routeScope, nameof(routeScope));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        DefinitionSha256 = definitionSha256 ?? throw new ArgumentNullException(nameof(definitionSha256));
        var expectedHash = Hash(definition);
        if (DefinitionSha256 != expectedHash)
            throw new ArgumentException("Definition hash does not match the policy definition.", nameof(definitionSha256));
        Enabled = enabled;
        CreatedAt = createdAt;
        CreatedBy = createdBy ?? throw new ArgumentNullException(nameof(createdBy));
    }

    public string TenantId { get; }
    public Guid PolicyId { get; }
    public int Version { get; }
    public string Name { get; }
    public string RouteScope { get; }
    public EntitySyncPolicyDefinition Definition { get; }
    public EntitySyncSha256 DefinitionSha256 { get; }
    public bool Enabled { get; }
    public DateTimeOffset CreatedAt { get; }
    public EntitySyncActor CreatedBy { get; }

    public static EntitySyncPolicy Create(
        string tenantId,
        Guid policyId,
        string name,
        string routeScope,
        EntitySyncPolicyDefinition definition,
        bool enabled,
        DateTimeOffset now,
        EntitySyncActor actor) =>
        new(tenantId, policyId, 1, name, routeScope, definition, Hash(definition), enabled, now, actor);

    public EntitySyncPolicy NextVersion(
        EntitySyncActor actor,
        EntitySyncPolicyDefinition definition,
        DateTimeOffset now,
        bool? enabled = null) =>
        new(
            TenantId,
            PolicyId,
            checked(Version + 1),
            Name,
            RouteScope,
            definition,
            Hash(definition),
            enabled ?? Enabled,
            now,
            actor);

    private static EntitySyncSha256 Hash(EntitySyncPolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(definition.ToCanonicalJson()));
        return new EntitySyncSha256(Convert.ToHexString(bytes));
    }
}
