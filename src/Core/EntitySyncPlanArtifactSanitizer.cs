using System.Text.Json;

namespace LISSTech.EntitySync.Core;

internal static class EntitySyncPlanArtifactSanitizer
{
    // Credential-shaped identifier components the sanitizer redacts from
    // ExternalIds/CustomFields keys and Reasons strings.
    private static readonly string[] SensitiveTerms =
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

    // Treats a value as sensitive only when one of the credential terms appears as a
    // discrete identifier component. The match must not be preceded by a lowercase
    // letter (which would mean the term is the tail of a longer word such as
    // "reauthorization") and must not be followed by a lowercase letter or whitespace
    // (which would mean the term is the head of a longer word such as "tokenization"
    // or part of an English phrase such as "password reset pending"). A lowercase
    // letter is allowed immediately before the term when the term runs to the end of
    // the string, since "NCentralRegistrationToken" or "LCATBearerToken" are valid
    // credential-shaped keys whose trailing CamelCase component would otherwise be
    // misread as word continuation.
    private static bool IsSensitiveName(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var term in SensitiveTerms)
        {
            var searchStart = 0;
            while (searchStart <= value.Length - term.Length)
            {
                var index = value.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;

                if (IsAtIdentifierBoundary(value, index, term.Length)) return true;
                searchStart = index + 1;
            }
        }
        return false;
    }

    private static bool IsAtIdentifierBoundary(string value, int start, int length)
    {
        var endIndex = start + length;
        var atEnd = endIndex >= value.Length;

        if (start > 0 && char.IsLower(value[start - 1]) && !atEnd) return false;

        if (!atEnd)
        {
            var next = value[endIndex];
            if (char.IsLower(next) || char.IsWhiteSpace(next)) return false;
        }

        return true;
    }
}