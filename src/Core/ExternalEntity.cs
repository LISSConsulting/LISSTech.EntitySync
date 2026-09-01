namespace LISSTech.EntitySync.Core;

public sealed class ExternalEntity
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? ParentEntityType { get; set; }
    public long? Version { get; set; }
    public string? LifecycleStatus { get; set; }
    public bool IsDeleted { get; set; }
    public string? MergeSurvivorId { get; set; }
    public List<string> MergeDonorIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<ExternalEntity> Children { get; set; } = [];
    public List<ExternalPlatformLink> PlatformLinks { get; set; } = [];
    public Dictionary<string, string> ExternalIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Name { get; set; } = string.Empty;
    public string NormalizedName => EntityNormalizer.NormalizeName(Name);
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? PrimarySiteId { get; set; }
    public string? PrimarySiteName { get; set; }
    public EntityAddress? PrimaryAddress { get; set; }
    public EntityAddress? BillingAddress { get; set; }
    public EntityAddress? ShippingAddress { get; set; }
    public bool? IsActive { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetExternalId(string name)
    {
        return ExternalIds.TryGetValue(name, out var value) ? value : null;
    }

    public string? GetCustomField(string name)
    {
        return CustomFields.TryGetValue(name, out var value) ? value : null;
    }
}

public sealed class ExternalPlatformLink
{
    public string PlatformInstanceId { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
}
