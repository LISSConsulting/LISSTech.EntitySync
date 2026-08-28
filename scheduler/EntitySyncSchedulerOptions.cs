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

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(12);
}
