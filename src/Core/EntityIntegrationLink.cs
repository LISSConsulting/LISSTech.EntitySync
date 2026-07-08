namespace LISSTech.EntitySync.Core;

public sealed class EntityIntegrationLink
{
    public string SourceVendor { get; set; } = string.Empty;
    public string SourceEntityType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string TargetVendor { get; set; } = string.Empty;
    public string TargetEntityType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string IntegrationId { get; set; } = string.Empty;
    public string LinkId { get; set; } = string.Empty;
    public string? ParentTargetId { get; set; }
    public bool Primary { get; set; }
}
