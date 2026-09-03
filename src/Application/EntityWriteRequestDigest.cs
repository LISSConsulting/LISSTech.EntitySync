using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public static class EntityWriteRequestDigest
{
    public const int SchemaVersion = 2;

    public static string Compute(EntityWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = SchemaVersion,
            ["vendor"] = request.Vendor,
            ["entityType"] = request.EntityType,
            ["id"] = request.Id,
            ["primarySiteId"] = request.PrimarySiteId,
            ["parentId"] = request.ParentId,
            ["parentEntityType"] = request.ParentEntityType,
            ["parentClientId"] = request.ParentClientId,
            ["name"] = request.Name,
            ["address"] = CanonicalizeAddress(request.Address),
            ["resolvedParent"] = request.ResolvedParent is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["clientId"] = request.ResolvedParent.ClientId,
                    ["siteId"] = request.ResolvedParent.SiteId,
                    ["parentEntityType"] = request.ResolvedParent.ParentEntityType,
                    ["sourcePlatformInstanceId"] =
                        request.ResolvedParent.SourcePlatformInstanceId,
                    ["matchedLinkExternalId"] =
                        request.ResolvedParent.MatchedLinkExternalId,
                    ["matchedLinkStatus"] =
                        request.ResolvedParent.MatchedLinkStatus,
                    ["matchedLinkToken"] =
                        request.ResolvedParent.MatchedLinkToken,
                    ["observedOwnerVersion"] =
                        request.ResolvedParent.ObservedOwnerVersion
                },
            ["customFieldOnly"] = request.CustomFieldOnly,
            ["fields"] = Canonicalize(request.Fields),
            ["customFields"] = Canonicalize(request.CustomFields)
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static object? CanonicalizeAddress(EntityAddress? address) =>
        address is null
            ? null
            : new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["addressType"] = address.AddressType,
                ["attention"] = address.Attention,
                ["line1"] = address.Line1,
                ["line2"] = address.Line2,
                ["line3"] = address.Line3,
                ["city"] = address.City,
                ["state"] = address.State,
                ["postalCode"] = address.PostalCode,
                ["country"] = address.Country
            };

    private static object? Canonicalize(object? value)
    {
        if (value is null or bool or string) return value;
        if (value is IDictionary dictionary) return CanonicalizeDictionary(dictionary);
        if (value is IEnumerable enumerable) return enumerable.Cast<object?>().Select(Canonicalize).ToList();
        return value;
    }

    private static SortedDictionary<string, object?> CanonicalizeDictionary(IDictionary dictionary)
    {
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
                throw new ArgumentException("Write request field dictionaries must have string keys.", nameof(dictionary));
            canonical[key] = Canonicalize(entry.Value);
        }
        return canonical;
    }

}
