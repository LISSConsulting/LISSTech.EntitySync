using System.Text.RegularExpressions;

namespace LISSTech.EntitySync.Core;

public static partial class EntityScopeSlug
{
    public static bool IsValid(string? slug) => !string.IsNullOrEmpty(slug) && SlugPattern().IsMatch(slug);

    public static string Derive(string? displayName, string? fallbackId)
    {
        var basis = !string.IsNullOrWhiteSpace(displayName) ? displayName : fallbackId ?? string.Empty;
        var slug = ToCandidate(basis);
        if (IsValid(slug)) return slug;

        var fallbackIdSlug = ToCandidate(fallbackId);
        if (!IsValid(fallbackIdSlug)) return $"customer-{fallbackId}".Trim('-');
        var fallbackSlug = ToCandidate($"customer {fallbackIdSlug}");
        return IsValid(fallbackSlug) ? fallbackSlug : $"customer-{fallbackId}".Trim('-');
    }

    private static string ToCandidate(string? value)
    {
        var slug = SlugSeparatorPattern().Replace(value ?? string.Empty, "-").Trim('-');
        if (slug.Length > 64) slug = slug[..64].Trim('-');
        return slug;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$", RegexOptions.Compiled)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex SlugSeparatorPattern();
}
