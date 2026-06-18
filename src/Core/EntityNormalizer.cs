using System.Text.RegularExpressions;

namespace LISSTech.EntitySync.Core;

public static partial class EntityNormalizer
{
    private static readonly HashSet<string> LeadingArticles = new(StringComparer.OrdinalIgnoreCase) { "the" };

    // Legal/corporate suffix terms are derived from cleanco's organization type
    // term database: https://github.com/psolin/cleanco (MIT License).
    // Match from the end only so words like "limited" or "company" stay meaningful
    // when they are part of the actual name.
    private static readonly string[][] LegalSuffixes = BuildSuffixes(
        "3at", "a d", "a e", "a g", "a s", "a spol", "aat", "ab", "ae", "ag", "aj", "akc spol",
        "akciova spolecnost", "aktiengesellschaft", "ans", "aps", "as", "asa", "association des coproprietaires de",
        "association des copropriétaires de", "ay", "b v", "bhd", "bt", "bv", "bvba", "charity", "cio", "co",
        "comm v", "company", "corp", "corporation", "cpt", "c v", "cv", "d d", "d n o", "d o o", "d o o e l",
        "da", "dat", "e c", "e e", "e g", "e k", "e u", "e v", "ee", "ehf", "eirl", "eingetragene genossenschaft mit beschrankter haftpflicht",
        "eingetragene genossenschaft mit beschränkter haftpflicht", "esv", "et", "eurl", "ev", "exploitation agricole a responsabilite limitee",
        "exploitation agricole à responsabilité limitée", "fie", "g m b h", "gbr", "gesellschaft mit beschrankter haftung",
        "gesellschaft mit beschränkter haftung", "gesmbh", "gie", "gmbh", "gmbh co kg", "gmbh and co kg", "gp",
        "gte", "hb", "hf", "i s", "ij", "inc", "incorporated", "j t d", "k d", "k d a", "k gaa", "k s",
        "kabushiki kaisha", "kb", "kd", "kda", "kft", "kg", "kht", "komanditna spolocnost", "komanditná spoločnosť",
        "komanditni spolecnost", "komanditní společnost", "kommanditgesellschaft", "ks", "kt", "kv", "ky",
        "l l c", "l l l p", "l l p", "l p", "l t d", "lda", "limited", "llc", "lllp", "llp", "lp",
        "ltd", "ltda", "mb", "mchj", "n v", "nl", "nuf", "nv", "nyrt", "o d", "o e", "oaj", "oao", "od",
        "oe", "og", "ood", "ooo", "oy", "oyj", "p c", "p l c", "p l l c", "p s", "partnership", "pc",
        "plc", "pllc", "pp", "private", "pryvatne pidpryyemstvo", "pt", "pte", "pte ltd", "pty ltd", "qk",
        "rt", "s a", "s a de c v", "s a p a", "s a r l", "s a s", "s c", "s c a", "s c p a", "s c s",
        "s cra", "s de r l", "s de r l de c v", "s de rl", "s de rl de cv", "s en c", "s f", "s k a",
        "s l", "s l n e", "s m b a", "s n c", "s p", "s r l", "s r o", "s s", "sa", "sabiedriba ar ierobezotu atbildibu",
        "sabiedrība ar ierobežotu atbildību", "sae", "sal", "saoc", "saog", "sarl", "sasu", "sc", "sca",
        "scs", "sd", "sdn bhd", "se", "secs", "ses", "sh a", "sicav", "sl", "slne", "smba", "snc",
        "soc col", "sociedad anonima", "sociedad limitada", "sp j", "sp k", "sp p", "sp z o o", "sp z oo",
        "spol s r o", "spol s ro", "spol sro", "spolecenstvi vlastniku jednotek", "společenství vlastníků jednotek",
        "spolecnost s rucenim omezenym", "společnost s ručením omezeným", "spolocnost s rucenim obmedzenym",
        "spoločnosť s ručením obmedzeným", "spolka komandytowa", "spolka z ograniczona odpowiedzialnoscia",
        "spółka komandytowa", "spółka z ograniczoną odpowiedzialnością", "sprl", "srl", "sro", "stg",
        "syndicat de la copropriete du", "syndicat de la copropriété du", "tapui", "teo", "tmi", "tov",
        "tovarystvo z dodatkvoiu vidpovidalnistiu", "tovarystvo z obmezhenoiu vidpovidalnistiu", "trust", "uc",
        "uab", "ultd", "unlimited", "unltd", "vat", "vereniging van mede eigenaars van", "vof", "vos", "vzw",
        "xk", "xt", "yoaj", "zat", "zrt");

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
