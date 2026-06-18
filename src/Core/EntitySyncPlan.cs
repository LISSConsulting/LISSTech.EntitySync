namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncPlan
{
    public string SourceVendor { get; set; } = string.Empty;
    public string SourceEntityType { get; set; } = string.Empty;
    public string TargetVendor { get; set; } = string.Empty;
    public string TargetEntityType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<EntitySyncPlanItem> Items { get; set; } = new();
}
