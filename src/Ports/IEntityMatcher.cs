using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityMatcher
{
    IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source, IReadOnlyList<ExternalEntity> targets, MatchOptions options);
}
