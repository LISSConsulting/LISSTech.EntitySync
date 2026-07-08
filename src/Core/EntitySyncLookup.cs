namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncLookup
{
    public string Vendor { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string?> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
