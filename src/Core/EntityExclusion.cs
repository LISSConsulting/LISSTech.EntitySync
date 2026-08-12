namespace LISSTech.EntitySync.Core;

public sealed record EntityExclusionRoute(
    string TenantId,
    string SourceVendor,
    string SourceConnectionId,
    string SourceEntityType,
    string TargetVendor,
    string TargetConnectionId,
    string TargetEntityType)
{
    public static EntityExclusionRoute Create(
        string tenantId,
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType)
    {
        return new EntityExclusionRoute(
            Require(tenantId, nameof(tenantId), 256),
            EntitySyncVendors.Normalize(Require(sourceVendor, nameof(sourceVendor), 64)),
            Require(sourceConnectionId, nameof(sourceConnectionId), 64),
            Require(sourceEntityType, nameof(sourceEntityType), 64),
            EntitySyncVendors.Normalize(Require(targetVendor, nameof(targetVendor), 64)),
            Require(targetConnectionId, nameof(targetConnectionId), 64),
            Require(targetEntityType, nameof(targetEntityType), 64));
    }

    private static string Require(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength) throw new ArgumentException($"{name} cannot exceed {maximumLength} characters.", name);
        return trimmed;
    }
}

public sealed record EntityExclusion(
    Guid Id,
    EntityExclusionRoute Route,
    string SourceEntityId,
    string SourceName,
    string Reason,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? RevokedBy = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActive => RevokedAt is null;
}
