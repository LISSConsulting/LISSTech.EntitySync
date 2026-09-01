using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Mapping;

public sealed partial class DefaultEntityMapper : IEntityMapper
{
    public EntityWriteRequest MapCreate(
        ExternalEntity source,
        string targetVendor,
        string targetEntityType,
        MatchOptions options) =>
        MapCreate(source, targetVendor, targetEntityType, options, null);

    public EntityWriteRequest MapCreate(
        ExternalEntity source,
        string targetVendor,
        string targetEntityType,
        MatchOptions options,
        EntityWriteParent? resolvedParent)
    {
        targetVendor = EntitySyncVendors.Normalize(targetVendor);
        if (resolvedParent is not null
            && !EntitySyncVendors.IsOrchestraMSP(targetVendor))
            throw new UnsupportedEntityWriteParentMappingException();
        var request = new EntityWriteRequest
        {
            Vendor = targetVendor,
            EntityType = targetEntityType,
            Name = source.Name,
            ParentId = EntitySyncVendors.IsOrchestraMSP(targetVendor)
                ? null
                : source.ParentId,
            ParentEntityType = EntitySyncVendors.IsOrchestraMSP(targetVendor)
                ? null
                : source.ParentEntityType,
            PrimarySiteId = source.PrimarySiteId,
            Address = MapAddress(source)
        };
        AddCommonHaloFields(request, source);
        AddTargetCustomField(request, source, targetVendor, options);
        AddNCentralSourceFields(request, source, targetVendor);
        AddHaloNetSuiteMetadata(request, source, targetVendor);
        AddNCentralLinkMarker(request, source, targetVendor);
        AddLtacCustomerScopeFields(request, source, targetVendor);
        AddOrchestraFields(request, source, targetVendor);
        if (EntitySyncVendors.IsOrchestraMSP(targetVendor))
            ApplyResolvedParent(request, targetEntityType, resolvedParent);
        return request;
    }

    public EntityWriteRequest MapUpdate(ExternalEntity source, ExternalEntity target, MatchOptions options)
    {
        var targetVendor = EntitySyncVendors.Normalize(target.Vendor);
        var request = new EntityWriteRequest
        {
            Vendor = targetVendor,
            EntityType = target.EntityType,
            Id = target.Id,
            ParentId = target.ParentId,
            ParentEntityType = target.ParentEntityType,
            ParentClientId = target.GetExternalId("OrchestraClientId"),
            PrimarySiteId = target.PrimarySiteId,
            ExpectedVersion = target.Version,
            Name = source.Name,
            Address = MapAddress(source)
        };
        AddCommonHaloFields(request, source);
        AddTargetCustomField(request, source, targetVendor, options);
        AddNCentralSourceFields(request, source, targetVendor);
        AddHaloNetSuiteMetadata(request, source, targetVendor);
        AddNCentralLinkMarker(request, source, targetVendor);
        AddLtacCustomerScopeFields(request, source, targetVendor);
        AddOrchestraFields(request, source, targetVendor);
        return request;
    }

    private static void AddOrchestraFields(
        EntityWriteRequest request,
        ExternalEntity source,
        string targetVendor)
    {
        if (!EntitySyncVendors.IsOrchestraMSP(targetVendor)) return;

        foreach (var field in source.CustomFields
                     .Where(pair => pair.Value is not null
                                    && !pair.Key.Equals(
                                        "address_type",
                                        StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            request.Fields[field.Key] = field.Value;
    }

    private static void ApplyResolvedParent(
        EntityWriteRequest request,
        string targetEntityType,
        EntityWriteParent? parent)
    {
        if (targetEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase))
            return;
        if (parent is null)
            return;
        request.ResolvedParent = parent;
        request.ParentClientId = parent.ClientId.ToString("D");
        request.ParentEntityType = parent.ParentEntityType;
        request.ParentId = parent.ParentEntityType.Equals(
            "Site", StringComparison.OrdinalIgnoreCase)
            ? parent.SiteId?.ToString("D")
            : parent.ClientId.ToString("D");
    }

    private static EntityAddress? MapAddress(ExternalEntity source)
    {
        if (!source.EntityType.Equals("Address", StringComparison.OrdinalIgnoreCase))
            return null;
        var value = source.PrimaryAddress;
        if (value is null) return null;
        return new EntityAddress
        {
            AddressType = string.IsNullOrWhiteSpace(value.AddressType)
                ? source.Name
                : value.AddressType,
            Attention = value.Attention,
            Line1 = value.Line1,
            Line2 = value.Line2,
            Line3 = value.Line3,
            City = value.City,
            State = value.State,
            PostalCode = value.PostalCode,
            Country = value.Country
        };
    }

    private static void AddLtacCustomerScopeFields(EntityWriteRequest request, ExternalEntity source, string targetVendor)
    {
        if (!EntitySyncVendors.IsAgentController(targetVendor)) return;
        if (!source.Vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) return;

        if (source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            var ncentralSiteId = FirstNonEmpty(source.GetExternalId("NCentralSiteId"), source.Id);
            var ncentralParentCustomerId = source.GetExternalId("NCentralCustomerId");
            var parentCustomerName = source.GetCustomField("NCentralCustomerName");
            request.Fields["display_name"] = source.Name;
            if (!string.IsNullOrWhiteSpace(ncentralSiteId)) request.Fields["ncentral_customer_id"] = ncentralSiteId;
            if (!string.IsNullOrWhiteSpace(ncentralParentCustomerId)) request.Fields["ncentral_parent_customer_id"] = ncentralParentCustomerId;
            var parentContext = FirstNonEmpty(parentCustomerName, ncentralParentCustomerId);
            var slugBasis = string.IsNullOrWhiteSpace(parentContext) ? source.Name : $"{parentContext} {source.Name}";
            request.Fields["slug"] = DeriveLtacSlug(slugBasis, ncentralSiteId);
            return;
        }

        if (!source.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) return;

        var ncentralCustomerId = FirstNonEmpty(source.GetExternalId("NCentralCustomerId"), source.Id);
        request.Fields["display_name"] = source.Name;
        if (!string.IsNullOrWhiteSpace(ncentralCustomerId)) request.Fields["ncentral_customer_id"] = ncentralCustomerId;
        request.Fields["slug"] = DeriveLtacSlug(source.Name, ncentralCustomerId);
    }

    private static void AddTargetCustomField(EntityWriteRequest request, ExternalEntity source, string targetVendor, MatchOptions options)
    {
        if (!targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return;
        var id = source.GetExternalId(options.SourceExternalIdName) ?? source.Id;
        if (!string.IsNullOrWhiteSpace(id)) request.CustomFields[options.TargetCustomFieldName] = id;
    }

    private static void AddHaloNetSuiteMetadata(EntityWriteRequest request, ExternalEntity source, string targetVendor)
    {
        if (!targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return;
        if (!source.Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase) || !source.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrWhiteSpace(source.Name)) request.CustomFields["CFNetSuiteCustomerName"] = source.Name;
    }

    private static void AddNCentralLinkMarker(EntityWriteRequest request, ExternalEntity source, string targetVendor)
    {
        if (!targetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) return;
        if (!source.Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(source.Id)) return;
        if (source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            request.CustomFields["externalId"] = source.Id;
            request.CustomFields["HaloPsaSiteId"] = source.Id;
            var nCentralCustomerId = source.GetExternalId("NCentralCustomerId") ?? source.GetCustomField("NCentralCustomerId");
            if (!string.IsNullOrWhiteSpace(nCentralCustomerId)) request.CustomFields["NCentralCustomerId"] = nCentralCustomerId;
            return;
        }

        if (!source.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) return;
        request.CustomFields["externalId"] = source.Id;
        request.CustomFields["HaloPsaId"] = source.Id;
        var netSuiteId = source.GetExternalId("NetSuiteInternalId") ?? source.GetCustomField("NetSuiteInternalId") ?? source.GetCustomField("CFNetSuiteCustomerID");
        if (!string.IsNullOrWhiteSpace(netSuiteId)) request.CustomFields["NetSuiteId"] = netSuiteId;
        var netSuiteName = source.GetCustomField("NetSuiteCustomerName") ?? source.GetCustomField("CFNetSuiteCustomerName") ?? source.GetCustomField("NetSuiteName");
        if (!string.IsNullOrWhiteSpace(netSuiteName)) request.CustomFields["NetSuiteCustomerName"] = netSuiteName;
    }

    private static void AddNCentralSourceFields(EntityWriteRequest request, ExternalEntity source, string targetVendor)
    {
        if (!targetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrWhiteSpace(source.Email)) request.Fields["contactEmail"] = source.Email;
        if (!string.IsNullOrWhiteSpace(source.Phone))
        {
            request.Fields["phone"] = source.Phone;
            request.Fields["contactPhone"] = source.Phone;
        }

        var firstName = FirstNonEmpty(
            source.GetCustomField("PrimaryContactFirstName"),
            source.GetCustomField("ContactFirstName"),
            source.GetCustomField("contactFirstName"),
            source.GetCustomField("firstname"));
        var lastName = FirstNonEmpty(
            source.GetCustomField("PrimaryContactLastName"),
            source.GetCustomField("ContactLastName"),
            source.GetCustomField("contactLastName"),
            source.GetCustomField("lastname"));
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            var contactName = FirstNonEmpty(source.GetCustomField("PrimaryContactName"), source.GetCustomField("ContactName"), source.PrimaryAddress?.Attention);
            var split = SplitContactName(contactName);
            firstName ??= split.FirstName;
            lastName ??= split.LastName;
        }

        if (!string.IsNullOrWhiteSpace(firstName)) request.Fields["contactFirstName"] = firstName;
        if (!string.IsNullOrWhiteSpace(lastName)) request.Fields["contactLastName"] = lastName;

        var address = !IsAddressEmpty(source.PrimaryAddress) ? source.PrimaryAddress : !IsAddressEmpty(source.ShippingAddress) ? source.ShippingAddress : source.BillingAddress;
        if (!IsAddressEmpty(address)) request.Fields["address"] = ToNCentralAddress(address!);
    }

    private static void AddCommonHaloFields(EntityWriteRequest request, ExternalEntity source)
    {
        request.Fields["clientsite_name"] = "Primary Address";
        if (!string.IsNullOrWhiteSpace(source.Website)) request.Fields["website"] = source.Website;
        if (!string.IsNullOrWhiteSpace(source.Domain)) request.Fields["domain"] = source.Domain;
        if (!string.IsNullOrWhiteSpace(source.Email)) request.Fields["contactemail"] = source.Email;
        if (!string.IsNullOrWhiteSpace(source.Phone)) request.Fields["phonenumber"] = source.Phone;
        var relationship = NetSuiteRelationship(source.GetCustomField("NetSuiteEntityStatusDisplay"));
        if (!string.IsNullOrWhiteSpace(relationship)) request.Fields["customer_relationship_name"] = relationship;
        var customerType = source.GetCustomField("NetSuiteCategoryDisplay");
        if (!string.IsNullOrWhiteSpace(customerType)) request.Fields["customer_type_name"] = customerType;
        if (source.ShippingAddress != null && !string.IsNullOrWhiteSpace(source.ShippingAddress.Compact())) request.Fields["delivery_address"] = ToHaloAddress(source.ShippingAddress);
        if (source.BillingAddress != null && !string.IsNullOrWhiteSpace(source.BillingAddress.Compact())) request.Fields["invoice_address"] = ToHaloAddress(source.BillingAddress);
        if (!request.Fields.ContainsKey("delivery_address") && source.PrimaryAddress != null && !string.IsNullOrWhiteSpace(source.PrimaryAddress.Compact())) request.Fields["delivery_address"] = ToHaloAddress(source.PrimaryAddress);
        if (!request.Fields.ContainsKey("invoice_address") && source.PrimaryAddress != null && !string.IsNullOrWhiteSpace(source.PrimaryAddress.Compact())) request.Fields["invoice_address"] = ToHaloAddress(source.PrimaryAddress);
    }

    private static Dictionary<string, object?> ToHaloAddress(EntityAddress address)
    {
        return new Dictionary<string, object?>
        {
            ["attention"] = address.Attention,
            ["line1"] = address.Line1,
            ["line2"] = address.Line2,
            ["line3"] = address.City,
            ["line4"] = address.State,
            ["postcode"] = address.PostalCode,
            ["country"] = address.Country
        };
    }

    private static Dictionary<string, object?> ToNCentralAddress(EntityAddress address)
    {
        return new Dictionary<string, object?>
        {
            ["street1"] = address.Line1,
            ["street2"] = address.Line2,
            ["city"] = address.City,
            ["stateProv"] = address.State,
            ["postalCode"] = address.PostalCode,
            ["country"] = address.Country
        };
    }

    private static bool IsAddressEmpty(EntityAddress? address) => address == null || string.IsNullOrWhiteSpace(address.Compact());

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private static (string? FirstName, string? LastName) SplitContactName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (null, null);
        if (parts.Length == 1) return (parts[0], null);
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    private static string? NetSuiteRelationship(string? entityStatusDisplay)
    {
        if (string.IsNullOrWhiteSpace(entityStatusDisplay)) return null;
        var value = entityStatusDisplay.Trim();
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        return separator > 0 ? value[..separator].Trim() : value;
    }

    // Matches the LTAC customer-scope slug contract in specs/001-ltac-sync-adapter/contracts/ltac-sync-rpc.md.
    internal static bool IsValidLtacSlug(string? slug) => EntityScopeSlug.IsValid(slug);

    internal static string DeriveLtacSlug(string? displayName, string? fallbackId)
    {
        return EntityScopeSlug.Derive(displayName, fallbackId);
    }
}
