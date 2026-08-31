using System.Collections.Frozen;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public static class EntityAdapterActions
{
    public const string Read = "Read";
    public const string Create = "Create";
    public const string Update = "Update";
}

public sealed record EntityTypeCapabilities
{
    public EntityTypeCapabilities(
        string entityType,
        IEnumerable<string> supportedActions,
        IEnumerable<string> supportedFields,
        IEnumerable<string> scheduledSafeFields)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("Entity type is required.", nameof(entityType));
        EntityType = entityType.Trim();
        SupportedActions = CopySet(supportedActions, nameof(supportedActions));
        SupportedFields = CopySet(supportedFields, nameof(supportedFields));
        ScheduledSafeFields = CopySet(scheduledSafeFields, nameof(scheduledSafeFields));
        if (!ScheduledSafeFields.IsSubsetOf(SupportedFields))
            throw new ArgumentException(
                "Scheduled-safe fields must be supported fields.",
                nameof(scheduledSafeFields));
    }

    public string EntityType { get; }
    public IReadOnlySet<string> SupportedActions { get; }
    public IReadOnlySet<string> SupportedFields { get; }
    public IReadOnlySet<string> ScheduledSafeFields { get; }

    public bool SupportsAction(string action) => SupportedActions.Contains(action);
    public bool SupportsField(string field) => SupportedFields.Contains(field);
    public bool IsScheduledSafe(string field) => ScheduledSafeFields.Contains(field);

    private static IReadOnlySet<string> CopySet(
        IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var result = values
            .Select(value => string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(
                    $"{parameterName} cannot contain an empty value.",
                    parameterName)
                : value.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}

public sealed record EntityAdapterCapabilities
{
    private readonly IReadOnlyDictionary<string, EntityTypeCapabilities> entityTypes;
    private readonly IReadOnlyCollection<EntityTypeCapabilities> entityTypeValues;

    public EntityAdapterCapabilities(
        string vendor,
        IEnumerable<EntityTypeCapabilities> entityTypes)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            throw new ArgumentException("Vendor is required.", nameof(vendor));
        Vendor = EntitySyncVendors.Normalize(vendor.Trim());
        ArgumentNullException.ThrowIfNull(entityTypes);
        entityTypeValues = entityTypes.ToArray();
        this.entityTypes = entityTypeValues.ToFrozenDictionary(
            capability => capability.EntityType,
            StringComparer.OrdinalIgnoreCase);
    }

    public string Vendor { get; }
    public IReadOnlyCollection<EntityTypeCapabilities> EntityTypes =>
        entityTypeValues;

    public bool TryGetEntityType(
        string entityType,
        out EntityTypeCapabilities capability) =>
        entityTypes.TryGetValue(entityType, out capability!);

    public static EntityAdapterCapabilities ForVendor(string vendor)
    {
        var normalized = EntitySyncVendors.Normalize(vendor);
        if (normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                normalized,
                [
                    Capability(
                        "Client",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create, EntityAdapterActions.Update],
                        ["Id", "ExternalId", "Name", "Phone", "Email", "Address", "IsActive"]),
                    Capability(
                        "Site",
                        [EntityAdapterActions.Read],
                        ["Id", "ExternalId", "Name", "Phone", "Email", "Address", "IsActive"])
                ]);
        }
        if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                normalized,
                [Capability(
                    "Customer",
                    [EntityAdapterActions.Read],
                    ["Id", "NetSuiteInternalId", "Name", "Phone", "Email", "Address", "IsActive"],
                    [])]);
        }
        if (normalized.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                normalized,
                [
                    Capability(
                        "Customer",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create, EntityAdapterActions.Update],
                        ["Id", "NCentralCustomerId", "Name", "Phone", "Email", "Address", "IsActive"]),
                    Capability(
                        "Site",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create],
                        ["Id", "NCentralSiteId", "Name", "Phone", "Email", "Address", "IsActive"])
                ]);
        }
        if (EntitySyncVendors.IsBillCom(normalized))
        {
            return new(
                normalized,
                [Capability(
                    "Client",
                    [EntityAdapterActions.Read, EntityAdapterActions.Create],
                    ["Id", "BillSpendClientId", "Name", "IsActive"])]);
        }
        if (EntitySyncVendors.IsAgentController(normalized))
        {
            return new(
                normalized,
                [Capability("Customer", [EntityAdapterActions.Read], ["Id", "Name"], [])]);
        }
        if (normalized.Equals("OrchestraMSP", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                normalized,
                [
                    Capability(
                        "Client",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create, EntityAdapterActions.Update],
                        ["Id", "Name", "Phone", "Email", "Address", "IsActive"]),
                    Capability(
                        "Site",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create, EntityAdapterActions.Update],
                        ["Id", "Name", "Phone", "Email", "Address", "IsActive"]),
                    Capability(
                        "Address",
                        [EntityAdapterActions.Read, EntityAdapterActions.Create, EntityAdapterActions.Update],
                        ["Id", "Address", "IsActive"])
                ]);
        }
        return new(normalized, []);
    }

    private static EntityTypeCapabilities Capability(
        string entityType,
        IEnumerable<string> actions,
        IEnumerable<string> fields,
        IEnumerable<string>? scheduledSafeFields = null) =>
        new(
            entityType,
            actions,
            fields,
            scheduledSafeFields ?? []);
}

public interface IEntityAdapter
{
    string Vendor { get; }
    IReadOnlyList<string> LookupTypes { get; }
    Task<EntityAdapterCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(EntityAdapterCapabilities.ForVendor(Vendor));
    Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
        EntityQuery query,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
        string type,
        CancellationToken cancellationToken);
    Task<EntityWriteResult> CreateEntityAsync(
        EntityWriteRequest request,
        CancellationToken cancellationToken);
    Task<EntityWriteResult> UpdateEntityAsync(
        EntityWriteRequest request,
        CancellationToken cancellationToken);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken);
}
