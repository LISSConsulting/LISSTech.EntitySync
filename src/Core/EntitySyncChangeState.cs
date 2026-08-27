namespace LISSTech.EntitySync.Core;

public sealed record EntitySyncChangeStateRoute(
    string TenantId,
    string Scope,
    string SourceVendor,
    string SourceConnectionId,
    string SourceEntityType,
    string TargetVendor,
    string TargetConnectionId,
    string TargetEntityType)
{
    public static EntitySyncChangeStateRoute Create(
        string tenantId,
        string scope,
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType)
    {
        return new EntitySyncChangeStateRoute(
            Require(tenantId, nameof(tenantId), 256),
            RequireHash(scope, nameof(scope)),
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

    private static string RequireHash(string value, string name)
    {
        var hash = Require(value, name, 64);
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException($"{name} must be a lowercase 64-character hexadecimal value.", name);
        return hash;
    }
}

public sealed record EntitySyncChangeState(
    EntitySyncChangeStateRoute Route,
    string SourceEntityId,
    string SourceName,
    string TargetEntityId,
    int HashVersion,
    string PayloadHash,
    DateTimeOffset AppliedAt);
