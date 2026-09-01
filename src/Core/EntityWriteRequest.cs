namespace LISSTech.EntitySync.Core;

public sealed class EntityWriteRequest
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public string? ParentEntityType { get; set; }
    public string? ParentClientId { get; set; }
    public long? ExpectedVersion { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PrimarySiteId { get; set; }
    public string? VendorRequestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public EntityAddress? Address { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EntityWriteParent(
    Guid ClientId,
    Guid? SiteId,
    string ParentEntityType);
