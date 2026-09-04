using System.Text.RegularExpressions;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Runtime;

public static class EntityCredentialSanitizer
{
    // Substring tokens that strongly imply a credential key. Matched against the
    // normalized (lowercased, separators collapsed) field name after a token-aware
    // split so substring hits inside unrelated words (e.g. "monkey", "turkey")
    // are correctly skipped.
    private static readonly string[] CredentialTokens =
    {
        "apikey", "api", "token", "password", "pwd", "passwd", "secret",
        "authorization", "auth", "credential", "private", "encryption"
    };
    // Tokens that denote the credential part of a name (the "what kind of secret").
    private static readonly string[] CredentialSuffixes =
    {
        "key", "secret", "password", "pwd", "passwd", "token",
        "credential", "cipher", "auth"
    };
    // Common English nouns that are NOT credentials but happen to contain a
    // credential substring. Substring matching against these skips the false
    // positive without touching the suffix check.
    private static readonly string[] SafeNonCredentialWords =
    {
        "monkey", "turkey", "donkey", "hockey", "jersey", "valley",
        "honey", "money", "donate", "donated", "donation",
        "subkey", "keynote", "keyword", "keyframe", "passport", "passage",
        "passing", "authored", "authority", "authorize", "authentic"
    };

    private static readonly Regex CredentialValuePattern = new(
        "^(?:"
        + "(?:sk|pk|rk)[_-][A-Za-z0-9]{16,}"
        + "|xox[baprs]-[A-Za-z0-9-]{10,}"
        + "|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}:[A-Za-z0-9._-]{8,}"
        + "|eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+"
        + ")$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a sanitized copy of <paramref name="entity"/> with credential-shaped keys
    /// (in <see cref="ExternalEntity.CustomFields"/> and
    /// <see cref="ExternalEntity.ExternalIds"/>) and credential-shaped values redacted.
    /// Children are preserved and recursively sanitized. When nothing changes, the
    /// original entity instance is returned.
    /// </summary>
    public static ExternalEntity Sanitize(ExternalEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var sanitizedCustom = ScrubNullableDictionary(entity.CustomFields);
        var sanitizedExternalIds = ScrubDictionary(entity.ExternalIds);
        var sanitizedChildren = ScrubChildren(entity.Children);

        if (ReferenceEquals(sanitizedCustom, entity.CustomFields)
            && ReferenceEquals(sanitizedExternalIds, entity.ExternalIds)
            && ReferenceEquals(sanitizedChildren, entity.Children))
        {
            return entity;
        }

        var copy = new ExternalEntity
        {
            Vendor = entity.Vendor,
            EntityType = entity.EntityType,
            Id = entity.Id,
            ParentId = entity.ParentId,
            ParentEntityType = entity.ParentEntityType,
            Version = entity.Version,
            LifecycleStatus = entity.LifecycleStatus,
            IsDeleted = entity.IsDeleted,
            MergeSurvivorId = entity.MergeSurvivorId,
            Name = entity.Name,
            Email = entity.Email,
            Phone = entity.Phone,
            Website = entity.Website,
            Domain = entity.Domain,
            PrimarySiteId = entity.PrimarySiteId,
            PrimarySiteName = entity.PrimarySiteName,
            PrimaryAddress = entity.PrimaryAddress,
            BillingAddress = entity.BillingAddress,
            ShippingAddress = entity.ShippingAddress,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
        foreach (var pair in entity.Tags) copy.Tags.Add(pair);
        foreach (var pair in sanitizedExternalIds) copy.ExternalIds[pair.Key] = pair.Value;
        foreach (var link in entity.PlatformLinks) copy.PlatformLinks.Add(link);
        foreach (var pair in sanitizedCustom) copy.CustomFields[pair.Key] = pair.Value;
        foreach (var child in sanitizedChildren) copy.Children.Add(child);
        return copy;
    }

    public static bool IsCredentialField(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return false;
        // Normalize separators and casing before any token or suffix comparison
        // so "API-Key", "api_key", and "API Key" all reduce to a single token stream.
        var tokens = Tokenize(fieldName);
        if (tokens.Length == 0) return false;
        if (SafeNonCredentialWords.Any(safe =>
                safe.Equals(tokens[^1], StringComparison.OrdinalIgnoreCase)))
            return false;
        // Suffix rule: the last token names a credential type.
        if (CredentialSuffixes.Any(suffix =>
                suffix.Equals(tokens[^1], StringComparison.OrdinalIgnoreCase)))
            return true;
        // Token rule: any token (other than the safe-word we already whitelisted)
        // matches a credential token. "monkey" -> ["monkey"], suffix check would
        // skip it because "monkey" is not a known credential suffix; token check
        // would also skip it because no token matches CredentialTokens.
        return tokens.Any(token => CredentialTokens.Any(cred =>
            cred.Equals(token, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool LooksLikeCredentialValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return CredentialValuePattern.IsMatch(value.Trim());
    }

    private static string[] Tokenize(string fieldName) =>
        fieldName.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, string> ScrubDictionary(
        Dictionary<string, string> source)
    {
        if (source.Count == 0) return source;
        Dictionary<string, string>? scrubbed = null;
        foreach (var pair in source)
        {
            var keyIsCredential = IsCredentialField(pair.Key);
            var valueIsCredential = LooksLikeCredentialValue(pair.Value);
            if (!keyIsCredential && !valueIsCredential) continue;
            scrubbed ??= new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
            scrubbed[pair.Key] = "[REDACTED]";
        }
        return scrubbed ?? source;
    }

    private static Dictionary<string, string?> ScrubNullableDictionary(
        Dictionary<string, string?> source)
    {
        if (source.Count == 0) return source;
        Dictionary<string, string?>? scrubbed = null;
        foreach (var pair in source)
        {
            var keyIsCredential = IsCredentialField(pair.Key);
            var valueIsCredential = LooksLikeCredentialValue(pair.Value);
            if (!keyIsCredential && !valueIsCredential) continue;
            scrubbed ??= new Dictionary<string, string?>(
                source, StringComparer.OrdinalIgnoreCase);
            scrubbed[pair.Key] = "[REDACTED]";
        }
        return scrubbed ?? source;
    }

    private static IReadOnlyList<ExternalEntity> ScrubChildren(
        IReadOnlyList<ExternalEntity> children)
    {
        if (children.Count == 0) return children;
        var replaced = false;
        var result = new List<ExternalEntity>(children.Count);
        foreach (var child in children)
        {
            var sanitized = Sanitize(child);
            if (!ReferenceEquals(sanitized, child)) replaced = true;
            result.Add(sanitized);
        }
        return replaced ? result : children;
    }
}
