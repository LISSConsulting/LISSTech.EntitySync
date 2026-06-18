using System.Text.Json;

namespace LISSTech.EntitySync.Adapters;

internal static class JsonObjectExtensions
{
    public static string? GetString(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String) return property.GetString();
                if (property.ValueKind == JsonValueKind.Number) return property.GetRawText();
                if (property.ValueKind == JsonValueKind.True) return "true";
                if (property.ValueKind == JsonValueKind.False) return "false";
            }
        }
        return null;
    }

    public static bool? GetBool(this JsonElement element, params string[] names)
    {
        var value = element.GetString(names);
        if (bool.TryParse(value, out var parsed)) return parsed;
        return null;
    }

    public static DateTimeOffset? GetDate(this JsonElement element, params string[] names)
    {
        var value = element.GetString(names);
        if (DateTimeOffset.TryParse(value, out var parsed)) return parsed;
        return null;
    }
}
