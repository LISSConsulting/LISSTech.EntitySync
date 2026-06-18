using System.Text.RegularExpressions;

namespace LISSTech.EntitySync.Core;

public static partial class EntityNormalizer
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "company", "corp", "corporation", "inc", "incorporated", "llc", "ltd", "limited", "lp", "llp", "pllc", "pc", "dba", "the"
    };

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = NonAlphaNumeric().Replace(value.ToLowerInvariant(), " ");
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !LegalSuffixes.Contains(t))
            .ToArray();
        return string.Join(" ", tokens);
    }

    public static string NormalizePhone(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : DigitsOnly().Replace(value, string.Empty);
    }

    public static string NormalizeDomain(string? website, string? email)
    {
        if (!string.IsNullOrWhiteSpace(website))
        {
            var candidate = website.Trim();
            if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return StripWww(uri.Host);
        }

        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@', StringComparison.Ordinal))
        {
            return StripWww(email.Split('@').Last());
        }

        return string.Empty;
    }

    public static string NormalizePostalCode(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : NonAlphaNumeric().Replace(value.ToUpperInvariant(), string.Empty);
    }

    private static string StripWww(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("\\D+", RegexOptions.Compiled)]
    private static partial Regex DigitsOnly();
}
