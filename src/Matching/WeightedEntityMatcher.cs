using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Matching;

public sealed class WeightedEntityMatcher : IEntityMatcher
{
    public IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source, IReadOnlyList<ExternalEntity> targets, MatchOptions options)
    {
        return targets.Select(target => Score(source, target, options))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Target.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    private static EntityMatchCandidate Score(ExternalEntity source, ExternalEntity target, MatchOptions options)
    {
        var reasons = new List<string>();
        var score = 0;
        var sourceExternalId = source.GetExternalId(options.SourceExternalIdName);
        var targetExternalId = target.GetExternalId(options.TargetExternalIdName) ?? target.GetCustomField(options.TargetCustomFieldName);

        if (!string.IsNullOrWhiteSpace(sourceExternalId) && string.Equals(sourceExternalId, targetExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return new EntityMatchCandidate
            {
                Source = source,
                Target = target,
                Score = 100,
                MatchType = "Linked",
                Reasons = { $"External ID match: {options.TargetCustomFieldName}={sourceExternalId}" }
            };
        }

        var nameScore = Similarity(source.NormalizedName, target.NormalizedName);
        if (nameScore >= 98) { score += 55; reasons.Add("Exact normalized name"); }
        else if (nameScore >= 90) { score += 45; reasons.Add($"Strong name similarity: {nameScore}"); }
        else if (nameScore >= 75) { score += 30; reasons.Add($"Name similarity: {nameScore}"); }
        else if (nameScore >= 60) { score += 15; reasons.Add($"Weak name similarity: {nameScore}"); }

        var sourceDomain = string.IsNullOrWhiteSpace(source.Domain) ? EntityNormalizer.NormalizeDomain(source.Website, source.Email) : source.Domain;
        var targetDomain = string.IsNullOrWhiteSpace(target.Domain) ? EntityNormalizer.NormalizeDomain(target.Website, target.Email) : target.Domain;
        if (!string.IsNullOrWhiteSpace(sourceDomain) && sourceDomain == targetDomain)
        {
            score += 25;
            reasons.Add("Domain match: " + sourceDomain);
        }

        var sourcePhone = EntityNormalizer.NormalizePhone(source.Phone);
        var targetPhone = EntityNormalizer.NormalizePhone(target.Phone);
        if (sourcePhone.Length >= 7 && sourcePhone == targetPhone)
        {
            score += 15;
            reasons.Add("Phone match");
        }

        var sourcePostal = EntityNormalizer.NormalizePostalCode(source.BillingAddress?.PostalCode);
        var targetPostal = EntityNormalizer.NormalizePostalCode(target.BillingAddress?.PostalCode);
        if (!string.IsNullOrWhiteSpace(sourcePostal) && sourcePostal == targetPostal)
        {
            score += 10;
            reasons.Add("Postal code match");
        }

        if (target.IsActive == false)
        {
            score = Math.Max(0, score - 10);
            reasons.Add("Inactive target penalty");
        }

        return new EntityMatchCandidate
        {
            Source = source,
            Target = target,
            Score = Math.Min(99, score),
            MatchType = score >= options.AutoLinkScore ? "HighConfidence" : score >= options.ReviewScore ? "NeedsReview" : "LowConfidence",
            Reasons = reasons
        };
    }

    private static int Similarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        if (left == right) return 100;
        var distance = Levenshtein(left, right);
        var max = Math.Max(left.Length, right.Length);
        return (int)Math.Round((1.0 - (double)distance / max) * 100);
    }

    private static int Levenshtein(string left, string right)
    {
        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) matrix[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) matrix[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost);
            }
        }
        return matrix[left.Length, right.Length];
    }
}
