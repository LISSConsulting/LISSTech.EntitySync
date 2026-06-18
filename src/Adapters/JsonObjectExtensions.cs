using System.Text.Json;

namespace LISSTech.EntitySync.Adapters;

internal static class JsonObjectExtensions
{
    public static string? GetString(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetPropertyIgnoreCase(name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String) return property.GetString();
                if (property.ValueKind == JsonValueKind.Number) return property.GetRawText();
                if (property.ValueKind == JsonValueKind.True) return "true";
                if (property.ValueKind == JsonValueKind.False) return "false";
            }
        }
        return null;
    }

    public static bool TryGetPropertyIgnoreCase(this JsonElement element, string name, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out property)) return true;
            foreach (var candidate in element.EnumerateObject())
            {
                if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    public static bool? GetBool(this JsonElement element, params string[] names)
    {
        var value = element.GetString(names);
        if (bool.TryParse(value, out var parsed)) return parsed;
        return null;
    }

    public static int? GetInt(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetPropertyIgnoreCase(name, out var property))
            {
                if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed)) return parsed;
                if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out parsed)) return parsed;
            }
        }

        return null;
    }

    public static DateTimeOffset? GetDate(this JsonElement element, params string[] names)
    {
        var value = element.GetString(names);
        if (DateTimeOffset.TryParse(value, out var parsed)) return parsed;
        return null;
    }
}
