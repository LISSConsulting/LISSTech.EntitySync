using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityMatcher
{
    IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source, IReadOnlyList<ExternalEntity> targets, MatchOptions options);
    IEntityMatchIndex CreateIndex(IReadOnlyList<ExternalEntity> targets, MatchOptions options);
}

public interface IEntityMatchIndex
{
    IReadOnlyList<EntityMatchCandidate> FindMatches(ExternalEntity source);
}
