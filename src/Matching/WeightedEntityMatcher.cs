using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Matching;

public sealed class WeightedEntityMatcher : IEntityMatcher
{
    public IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source, IReadOnlyList<ExternalEntity> targets, MatchOptions options)
    {
        return CreateIndex(targets, options).FindMatches(source);
    }

    public EntityMatchIndex CreateIndex(IReadOnlyList<ExternalEntity> targets, MatchOptions options) => new(targets, options);

    public sealed class EntityMatchIndex
    {
        private readonly MatchOptions options;
        private readonly TargetInfo[] targets;
        private readonly Dictionary<string, List<TargetInfo>> targetsByExternalId = new(StringComparer.OrdinalIgnoreCase);

        internal EntityMatchIndex(IReadOnlyList<ExternalEntity> targets, MatchOptions options)
        {
            this.options = options;
            this.targets = targets.Select(target => new TargetInfo(target, options)).ToArray();
            foreach (var target in this.targets)
            {
                if (string.IsNullOrWhiteSpace(target.ExternalId)) continue;
                if (!targetsByExternalId.TryGetValue(target.ExternalId, out var bucket))
                {
                    bucket = new List<TargetInfo>();
                    targetsByExternalId[target.ExternalId] = bucket;
                }

                bucket.Add(target);
            }
        }

        public IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source)
        {
            var sourceInfo = new SourceInfo(source, options);
            if (!string.IsNullOrWhiteSpace(sourceInfo.ExternalId) && targetsByExternalId.TryGetValue(sourceInfo.ExternalId, out var linkedTargets))
            {
                return linkedTargets
                    .Select(target => Linked(source, target.Entity, target.ExternalIdName, sourceInfo.ExternalId))
                    .OrderBy(candidate => candidate.Target.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray();
            }

            return targets.Select(target => Score(sourceInfo, target, options))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Target.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
        }
    }

    private sealed class SourceInfo
    {
        public SourceInfo(ExternalEntity entity, MatchOptions options)
        {
            Entity = entity;
            ExternalId = entity.GetExternalId(options.SourceExternalIdName);
            NormalizedName = EntityNormalizer.NormalizeName(entity.Name);
            Domain = string.IsNullOrWhiteSpace(entity.Domain) ? EntityNormalizer.NormalizeDomain(entity.Website, entity.Email) : entity.Domain;
            Phone = EntityNormalizer.NormalizePhone(entity.Phone);
            Postal = EntityNormalizer.NormalizePostalCode(entity.PrimaryAddress?.PostalCode);
        }

        public ExternalEntity Entity { get; }
        public string? ExternalId { get; }
        public string NormalizedName { get; }
        public string? Domain { get; }
        public string Phone { get; }
        public string Postal { get; }
    }

    private sealed class TargetInfo
    {
        public TargetInfo(ExternalEntity entity, MatchOptions options)
        {
            Entity = entity;
            ExternalId = entity.GetExternalId(options.TargetExternalIdName);
            ExternalIdName = options.TargetExternalIdName;
            if (string.IsNullOrWhiteSpace(ExternalId))
            {
                ExternalId = entity.GetCustomField(options.TargetCustomFieldName);
                ExternalIdName = options.TargetCustomFieldName;
            }
            NormalizedName = EntityNormalizer.NormalizeName(entity.Name);
            Domain = string.IsNullOrWhiteSpace(entity.Domain) ? EntityNormalizer.NormalizeDomain(entity.Website, entity.Email) : entity.Domain;
            Phone = EntityNormalizer.NormalizePhone(entity.Phone);
            Postal = EntityNormalizer.NormalizePostalCode(entity.PrimaryAddress?.PostalCode);
        }

        public ExternalEntity Entity { get; }
        public string? ExternalId { get; }
        public string ExternalIdName { get; }
        public string NormalizedName { get; }
        public string? Domain { get; }
        public string Phone { get; }
        public string Postal { get; }
    }

    private static EntityMatchCandidate Linked(ExternalEntity source, ExternalEntity target, string externalIdName, string sourceExternalId)
    {
        return new EntityMatchCandidate
        {
            Source = source,
            Target = target,
            Score = 100,
            MatchType = "Linked",
            Reasons = { $"External ID match: {externalIdName}={sourceExternalId}" }
        };
    }

    private static EntityMatchCandidate Score(SourceInfo source, TargetInfo target, MatchOptions options)
    {
        var reasons = new List<string>();
        var score = 0;

        var nameScore = Similarity(source.NormalizedName, target.NormalizedName);
        if (nameScore >= 98) { score += 70; reasons.Add("Exact normalized name"); }
        else if (nameScore >= 90) { score += 45; reasons.Add($"Strong name similarity: {nameScore}"); }
        else if (nameScore >= 75) { score += 30; reasons.Add($"Name similarity: {nameScore}"); }
        else if (nameScore >= 60) { score += 15; reasons.Add($"Weak name similarity: {nameScore}"); }

        if (!string.IsNullOrWhiteSpace(source.Domain) && source.Domain == target.Domain)
        {
            score += 25;
            reasons.Add("Domain match: " + source.Domain);
        }

        if (source.Phone.Length >= 7 && source.Phone == target.Phone)
        {
            score += 15;
            reasons.Add("Phone match");
        }

        if (!string.IsNullOrWhiteSpace(source.Postal) && source.Postal == target.Postal)
        {
            score += 10;
            reasons.Add("Postal code match");
        }

        if (target.Entity.IsActive == false)
        {
            score = Math.Max(0, score - 10);
            reasons.Add("Inactive target penalty");
        }

        return new EntityMatchCandidate
        {
            Source = source.Entity,
            Target = target.Entity,
            Score = Math.Min(99, score),
            MatchType = score >= options.AutoLinkScore ? "HighConfidence" : score >= options.ReviewScore ? "NeedsReview" : "LowConfidence",
            Reasons = reasons
        };
    }

    private static EntityMatchCandidate Score(ExternalEntity source, ExternalEntity target, MatchOptions options)
    {
        var sourceExternalId = source.GetExternalId(options.SourceExternalIdName);
        var targetExternalId = target.GetExternalId(options.TargetExternalIdName) ?? target.GetCustomField(options.TargetCustomFieldName);

        if (!string.IsNullOrWhiteSpace(sourceExternalId) && string.Equals(sourceExternalId, targetExternalId, StringComparison.OrdinalIgnoreCase))
        {
            var externalIdName = !string.IsNullOrWhiteSpace(target.GetExternalId(options.TargetExternalIdName)) ? options.TargetExternalIdName : options.TargetCustomFieldName;
            return Linked(source, target, externalIdName, sourceExternalId);
        }

        return Score(new SourceInfo(source, options), new TargetInfo(target, options), options);
    }

    private static IReadOnlyList<EntityMatchCandidate> LegacyFindMatches(ExternalEntity source, IReadOnlyList<ExternalEntity> targets, MatchOptions options)
    {
        return targets.Select(target => Score(source, target, options))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Target.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
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
