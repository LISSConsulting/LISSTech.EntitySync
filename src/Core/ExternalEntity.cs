using System.Management.Automation;

namespace LISSTech.EntitySync.Core;

public sealed class ExternalEntity
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, string> ExternalIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Name { get; set; } = string.Empty;
    public string NormalizedName => EntityNormalizer.NormalizeName(Name);
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? PrimarySiteId { get; set; }
    public string? PrimarySiteName { get; set; }
    public PSObject? PrimarySiteRaw { get; set; }
    public EntityAddress? BillingAddress { get; set; }
    public EntityAddress? ShippingAddress { get; set; }
    public bool? IsActive { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PSObject? Raw { get; set; }

    public string? GetExternalId(string name)
    {
        return ExternalIds.TryGetValue(name, out var value) ? value : null;
    }

    public string? GetCustomField(string name)
    {
        return CustomFields.TryGetValue(name, out var value) ? value : null;
    }
}
