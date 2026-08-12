using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityExclusionRepository
{
    Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(EntityExclusionRoute route, CancellationToken cancellationToken);

    Task<EntityExclusion> AddAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string sourceName,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string actor,
        CancellationToken cancellationToken);
}
