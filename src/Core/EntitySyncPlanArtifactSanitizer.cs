using System.Text.Json;

namespace LISSTech.EntitySync.Core;

internal static class EntitySyncPlanArtifactSanitizer
{
    private static readonly string[] SensitiveNameFragments =
    {
        "authorization",
        "bearer",
        "credential",
        "password",
        "secret",
        "token"
    };

    public static EntitySyncPlan Sanitize(EntitySyncPlan plan)
    {
        var json = JsonSerializer.Serialize(plan);
        var sanitized = JsonSerializer.Deserialize<EntitySyncPlan>(json)
            ?? throw new InvalidOperationException("EntitySync plan could not be prepared for export.");

        foreach (var item in sanitized.Items)
        {
            SanitizeEntity(item.Source);
            if (item.Target != null) SanitizeEntity(item.Target);
            for (var i = 0; i < item.Reasons.Count; i++)
            {
                if (IsSensitiveName(item.Reasons[i])) item.Reasons[i] = "[credential redacted]";
            }
        }

        foreach (var target in sanitized.TargetCandidates) SanitizeEntity(target);
        return sanitized;
    }

    private static void SanitizeEntity(ExternalEntity entity)
    {
        RemoveSensitiveEntries(entity.ExternalIds);
        RemoveSensitiveEntries(entity.CustomFields);
    }

    private static void RemoveSensitiveEntries<T>(IDictionary<string, T> values)
    {
        foreach (var key in values.Keys.Where(IsSensitiveName).ToArray())
        {
            values.Remove(key);
        }
    }

    private static bool IsSensitiveName(string value)
    {
        return SensitiveNameFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
