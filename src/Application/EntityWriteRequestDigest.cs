using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public static class EntityWriteRequestDigest
{
    public const int SchemaVersion = 1;

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
            ["name"] = request.Name,
            ["fields"] = Canonicalize(request.Fields),
            ["customFields"] = Canonicalize(request.CustomFields)
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static object? Canonicalize(object? value)
    {
        if (value is null or bool or string) return value;
        if (IsNumber(value)) return Convert.ToString(value, CultureInfo.InvariantCulture);
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

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong
            or Half or float or double or decimal or Int128 or UInt128;
}
