namespace LISSTech.EntitySync.Core;

public sealed class EntityWriteRequest
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
