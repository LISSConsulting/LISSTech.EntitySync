namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string SourceVendor { get; set; } = string.Empty;
    public string SourceEntityType { get; set; } = string.Empty;
    public string TargetVendor { get; set; } = string.Empty;
    public string TargetEntityType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(4);
    public string Status { get; set; } = EntitySyncPlanStatuses.Draft;
    public bool ReviewRequired { get; set; }
    public string? ApprovedDigest { get; set; }
    public EntitySyncPlanExecution Execution { get; set; } = new();
    public List<EntitySyncPlanItem> Items { get; set; } = new();
    public List<ExternalEntity> TargetCandidates { get; set; } = new();
}
