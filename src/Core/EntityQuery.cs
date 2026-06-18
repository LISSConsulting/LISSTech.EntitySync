namespace LISSTech.EntitySync.Core;

public sealed class EntityQuery
{
    public string EntityType { get; set; } = "Customer";
    public string? Search { get; set; }
    public bool IncludeInactive { get; set; }
    public int? Count { get; set; }
    public bool FullObjects { get; set; }
}
