using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Scheduler;

public sealed class EntitySyncSchedulerOptions
{
    public const string TenantId = "coolify-scheduler";
    public const string SourceConnectionId = "netsuite";
    public const string SourceVendor = "NetSuite";
    public const string SourceEntityType = "Customer";
    public const string TargetConnectionId = "halopsa";
    public const string TargetVendor = "HaloPSA";
    public const string TargetEntityType = "Client";

    public static EntitySyncSchedulerRoute NetSuiteToHalo { get; } = new(
        SourceConnectionId,
        SourceVendor,
        SourceEntityType,
        TargetConnectionId,
        TargetVendor,
        TargetEntityType);

    public static EntitySyncSchedulerRoute HaloToNCentral { get; } = new(
        TargetConnectionId,
        TargetVendor,
        TargetEntityType,
        "ncentral",
        "NCentral",
        "Customer");

    public static EntitySyncSchedulerRoute HaloToBillCom { get; } = new(
        TargetConnectionId,
        TargetVendor,
        TargetEntityType,
        "billcom",
        "Bill.com",
        "Client");

    public static EntitySyncSchedulerRoute HaloToSophosCentral { get; } = new(
        TargetConnectionId,
        TargetVendor,
        TargetEntityType,
        "sophos-central",
        "Sophos Central",
        "Customer",
        EntitySyncIntegrationContracts.SophosCentralTenantExternalIdName);

    public static IReadOnlyList<EntitySyncSchedulerRoute> FullChainRoutes { get; } =
    [
        NetSuiteToHalo,
        HaloToNCentral,
        HaloToBillCom,
        HaloToSophosCentral
    ];

    public EntitySyncSchedulerOptions()
        : this(FullChainRoutes)
    {
    }

    public EntitySyncSchedulerOptions(IReadOnlyList<EntitySyncSchedulerRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0) throw new ArgumentException("At least one scheduled synchronization route is required.", nameof(routes));
        Routes = routes.ToArray();
    }

    public IReadOnlyList<EntitySyncSchedulerRoute> Routes { get; }

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(12);
}

public sealed record EntitySyncSchedulerRoute(
    string SourceConnectionId,
    string SourceVendor,
    string SourceEntityType,
    string TargetConnectionId,
    string TargetVendor,
    string TargetEntityType,
    string? SourceExternalIdName = null);
