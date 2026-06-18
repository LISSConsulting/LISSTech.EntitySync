using System.Text.RegularExpressions;

namespace LISSTech.EntitySync.Core;

public static partial class EntityNormalizer
{
    private static readonly HashSet<string> LeadingArticles = new(StringComparer.OrdinalIgnoreCase) { "the" };

    // Curated legal/corporate suffix database. Match from the end only so words like
    // "limited" or "company" stay meaningful when they are part of the actual name.
    private static readonly string[][] LegalSuffixes = BuildSuffixes(
        "a g", "ab", "ag", "aktiengesellschaft", "aps", "as", "asa", "association", "bhd", "bv", "b v",
        "c corp", "c corporation", "ca", "charity", "cio", "co", "co ltd", "company", "corp", "corporation",
        "cv", "c v", "dba", "doing business as", "e k", "eirl", "eood", "ee", "eg", "ev", "e v",
        "foundation", "gbr", "gesmbh", "gmbh", "gmbh co kg", "gmbh and co kg", "gp", "hb", "holding",
        "holdings", "inc", "incorporated", "jsc", "kabushiki kaisha", "kg", "kk", "k k", "kommanditgesellschaft",
        "k s", "ks", "kft", "limited", "limited company", "llc", "l l c", "llp", "l l p", "lp", "l p", "ltd",
        "ltd co", "ltd company", "ltda", "ltee", "nv", "n v", "oao", "od", "ooo", "oy", "oyj", "p a",
        "partnership", "pc", "p c", "plc", "plc public limited company", "pllc", "p l l c", "pte", "pte ltd",
        "pty", "pty ltd", "public limited company", "pvt", "pvt ltd", "s a", "sa", "s a de c v", "sa de cv",
        "s de r l", "s de rl", "s de r l de c v", "s de rl de cv", "s en c", "s en c por a", "s r l", "s rl",
        "sarl", "s a r l", "sas", "s a s", "sc", "s c", "sca", "s c a", "scs", "s c s", "sdn bhd", "se",
        "sl", "s l", "slu", "s l u", "snc", "s n c", "sociedad anonima", "sociedad limitada", "sp z o o",
        "spolka z ograniczona odpowiedzialnoscia", "srl", "s r l", "sro", "s r o", "trust", "trustee", "uc", "unlimited",
        "vof", "v o f", "zrt");

    private static readonly int LongestLegalSuffixLength = LegalSuffixes.Max(s => s.Length);

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = NonAlphaNumeric().Replace(value.ToLowerInvariant(), " ");
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        while (tokens.Count > 0 && LeadingArticles.Contains(tokens[0])) tokens.RemoveAt(0);
        StripLegalSuffixes(tokens);
        return string.Join(" ", tokens);
    }

    private static void StripLegalSuffixes(List<string> tokens)
    {
        var removed = true;
        while (removed && tokens.Count > 1)
        {
            removed = false;
            for (var length = Math.Min(LongestLegalSuffixLength, tokens.Count - 1); length > 0; length--)
            {
                var start = tokens.Count - length;
                if (!LegalSuffixes.Any(suffix => suffix.Length == length && tokens.Skip(start).SequenceEqual(suffix, StringComparer.OrdinalIgnoreCase))) continue;
                tokens.RemoveRange(start, length);
                removed = true;
                break;
            }
        }
    }

    private static string[][] BuildSuffixes(params string[] suffixes)
    {
        return suffixes
            .Select(suffix => NonAlphaNumeric().Replace(suffix.ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(tokens => tokens.Length > 0)
            .DistinctBy(tokens => string.Join(" ", tokens), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(tokens => tokens.Length)
            .ToArray();
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
