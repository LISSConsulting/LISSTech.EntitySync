namespace LISSTech.EntitySync.Core;

public sealed class EntityWriteResult
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public object? Raw { get; set; }
}
