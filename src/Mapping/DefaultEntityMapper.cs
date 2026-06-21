using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Mapping;

public sealed class DefaultEntityMapper : IEntityMapper
{
    public EntityWriteRequest MapCreate(ExternalEntity source, string targetVendor, string targetEntityType, MatchOptions options)
    {
        var request = new EntityWriteRequest { Vendor = targetVendor, EntityType = targetEntityType, Name = source.Name };
        AddCommonHaloFields(request, source);
        var id = source.GetExternalId(options.SourceExternalIdName) ?? source.Id;
        request.CustomFields[options.TargetCustomFieldName] = id;
        return request;
    }

    public EntityWriteRequest MapUpdate(ExternalEntity source, ExternalEntity target, MatchOptions options)
    {
        var request = new EntityWriteRequest { Vendor = target.Vendor, EntityType = target.EntityType, Id = target.Id, PrimarySiteId = target.PrimarySiteId, Name = target.Name };
        AddCommonHaloFields(request, source);
        var id = source.GetExternalId(options.SourceExternalIdName) ?? source.Id;
        request.CustomFields[options.TargetCustomFieldName] = id;
        return request;
    }

    private static void AddCommonHaloFields(EntityWriteRequest request, ExternalEntity source)
    {
        request.Fields["clientsite_name"] = "Primary Address";
        if (!string.IsNullOrWhiteSpace(source.Phone)) request.Fields["phonenumber"] = source.Phone;
        if (source.PrimaryAddress != null)
        {
            request.Fields["delivery_address"] = new Dictionary<string, object?>
            {
                ["line1"] = source.PrimaryAddress.Line1,
                ["line2"] = source.PrimaryAddress.Line2,
                ["line3"] = source.PrimaryAddress.City,
                ["line4"] = source.PrimaryAddress.State,
                ["postcode"] = source.PrimaryAddress.PostalCode,
                ["country"] = source.PrimaryAddress.Country
            };
        }
    }
}
