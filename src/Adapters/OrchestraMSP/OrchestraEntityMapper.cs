using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Adapters.OrchestraMSP;

internal static class OrchestraEntityMapper
{
    public static ExternalEntity MapClient(OrchestraClientContract source)
    {
        ValidateIdentity(source.ClientId, source.Version, "client");
        var sites = source.Sites
            .OrderBy(site => site.SiteId)
            .Select(MapSite)
            .ToList();
        var addresses = source.Addresses
            .OrderBy(address => address.AddressId)
            .Select(MapAddress)
            .ToList();
        var result = new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Client",
            Id = source.ClientId.ToString("D"),
            Version = source.Version,
            Name = Require(source.Name, "client name"),
            LifecycleStatus = Require(source.LifecycleStatus, "client lifecycle status"),
            IsDeleted = source.IsDeleted,
            MergeSurvivorId = source.MergedIntoClientId?.ToString("D"),
            MergeDonorIds = source.MergedFromClientIds
                .Where(value => value != Guid.Empty)
                .Select(value => value.ToString("D"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            Tags = NormalizeTags(source.Tags),
            Children = sites.Concat(addresses).ToList(),
            PlatformLinks = MapLinks(source.PlatformLinks),
            CustomFields = MapFields(source.Fields)
        };
        result.ExternalIds["OrchestraClientId"] = result.Id;
        result.IsActive = IsActive(result.LifecycleStatus, result.IsDeleted,
            result.MergeSurvivorId);
        ApplyKnownFields(result, source.Fields);
        result.PrimaryAddress = addresses.FirstOrDefault(entity => entity.IsActive == true)
                                    ?.PrimaryAddress
                                ?? sites.SelectMany(site => site.Children)
                                    .FirstOrDefault(entity => entity.IsActive == true)
                                    ?.PrimaryAddress;
        result.BillingAddress = FindAddress(addresses, "billing");
        result.ShippingAddress = FindAddress(addresses, "shipping");
        var primarySite = sites.FirstOrDefault(site => site.IsActive == true);
        result.PrimarySiteId = primarySite?.Id;
        result.PrimarySiteName = primarySite?.Name;
        return result;
    }

    public static ExternalEntity MapSite(OrchestraSiteContract source)
    {
        ValidateIdentity(source.SiteId, source.Version, "site");
        if (source.ClientId == Guid.Empty)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        var addresses = source.Addresses
            .OrderBy(address => address.AddressId)
            .Select(MapAddress)
            .ToList();
        var result = new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Site",
            Id = source.SiteId.ToString("D"),
            ParentId = source.ClientId.ToString("D"),
            ParentEntityType = "Client",
            Version = source.Version,
            Name = Require(source.Name, "site name"),
            LifecycleStatus = Require(source.LifecycleStatus, "site lifecycle status"),
            IsDeleted = source.IsDeleted,
            Tags = NormalizeTags(source.Tags),
            Children = addresses,
            PlatformLinks = MapLinks(source.PlatformLinks),
            CustomFields = MapFields(source.Fields)
        };
        result.ExternalIds["OrchestraSiteId"] = result.Id;
        result.ExternalIds["OrchestraClientId"] = result.ParentId;
        result.IsActive = IsActive(result.LifecycleStatus, result.IsDeleted, null);
        ApplyKnownFields(result, source.Fields);
        result.PrimaryAddress = addresses.FirstOrDefault(entity => entity.IsActive == true)
            ?.PrimaryAddress;
        result.BillingAddress = FindAddress(addresses, "billing");
        result.ShippingAddress = FindAddress(addresses, "shipping");
        return result;
    }

    public static ExternalEntity MapAddress(OrchestraAddressContract source)
    {
        ValidateIdentity(source.AddressId, source.Version, "address");
        if (source.ClientId == Guid.Empty)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        var address = new EntityAddress
        {
            AddressType = source.AddressType,
            Attention = source.Attention,
            Line1 = source.Line1,
            Line2 = source.Line2,
            Line3 = source.Line3,
            City = source.City,
            State = source.State,
            PostalCode = source.PostalCode,
            Country = source.Country
        };
        var lifecycle = source.IsDeleted ? "deleted" : "active";
        var result = new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Address",
            Id = source.AddressId.ToString("D"),
            ParentId = (source.SiteId ?? source.ClientId).ToString("D"),
            ParentEntityType = source.SiteId.HasValue ? "Site" : "Client",
            Version = source.Version,
            Name = string.IsNullOrWhiteSpace(source.AddressType)
                ? address.Compact()
                : source.AddressType.Trim(),
            LifecycleStatus = lifecycle,
            IsDeleted = source.IsDeleted,
            IsActive = !source.IsDeleted,
            PrimaryAddress = address,
            Tags = NormalizeTags(source.Tags),
            PlatformLinks = MapLinks(source.PlatformLinks),
            CustomFields = MapFields(source.Fields)
        };
        result.ExternalIds["OrchestraAddressId"] = result.Id;
        result.ExternalIds["OrchestraClientId"] = source.ClientId.ToString("D");
        if (source.SiteId.HasValue)
            result.ExternalIds["OrchestraSiteId"] = source.SiteId.Value.ToString("D");
        result.CustomFields["address_type"] = source.AddressType;
        return result;
    }

    public static ExternalPlatformLink MapLink(OrchestraPlatformLinkContract source)
    {
        if (string.IsNullOrWhiteSpace(source.PlatformInstanceId)
            || string.IsNullOrWhiteSpace(source.Platform)
            || string.IsNullOrWhiteSpace(source.ExternalId)
            || string.IsNullOrWhiteSpace(source.Status)
            || string.IsNullOrWhiteSpace(source.EntityType)
            || source.EntityId == Guid.Empty)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        return new ExternalPlatformLink
        {
            PlatformInstanceId = source.PlatformInstanceId.Trim(),
            Platform = source.Platform.Trim(),
            ExternalId = source.ExternalId.Trim(),
            Status = source.Status.Trim(),
            EntityType = source.EntityType.Trim(),
            EntityId = source.EntityId.ToString("D")
        };
    }

    private static List<ExternalPlatformLink> MapLinks(
        IEnumerable<OrchestraPlatformLinkContract> links) =>
        links.Select(MapLink)
            .OrderBy(link => link.PlatformInstanceId, StringComparer.Ordinal)
            .ThenBy(link => link.ExternalId, StringComparer.Ordinal)
            .ThenBy(link => link.EntityType, StringComparer.Ordinal)
            .ThenBy(link => link.EntityId, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, string?> MapFields(
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in fields
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
            result.Add(pair.Key, CanonicalValue(pair.Value));
        }
        return result;
    }

    private static string? CanonicalValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => CanonicalJson(value),
        _ => throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID")
    };

    private static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void ApplyKnownFields(
        ExternalEntity entity,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        entity.Email = GetString(fields, "email");
        entity.Phone = GetString(fields, "phone");
        entity.Website = GetString(fields, "website");
        entity.Domain = GetString(fields, "domain");
    }

    private static string? GetString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string key) =>
        fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static EntityAddress? FindAddress(
        IEnumerable<ExternalEntity> addresses,
        string addressType) =>
        addresses.FirstOrDefault(entity =>
                entity.CustomFields.TryGetValue("address_type", out var value)
                && value != null
                && value.Equals(addressType, StringComparison.OrdinalIgnoreCase))
            ?.PrimaryAddress;

    private static List<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag, StringComparer.Ordinal)
            .ToList();

    private static bool IsActive(
        string lifecycle,
        bool deleted,
        string? mergeSurvivorId) =>
        !deleted
        && mergeSurvivorId is null
        && !lifecycle.Equals("inactive", StringComparison.OrdinalIgnoreCase)
        && !lifecycle.Equals("deleted", StringComparison.OrdinalIgnoreCase)
        && !lifecycle.Equals("merged", StringComparison.OrdinalIgnoreCase)
        && !lifecycle.Equals("disabled", StringComparison.OrdinalIgnoreCase);

    private static void ValidateIdentity(Guid id, long version, string entityType)
    {
        if (id == Guid.Empty || version <= 0 || string.IsNullOrWhiteSpace(entityType))
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
    }

    private static string Require(string? value, string description) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID")
            : value.Trim();
}
