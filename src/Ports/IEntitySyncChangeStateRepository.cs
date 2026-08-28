using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntitySyncChangeStateRepository
{
    Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
        EntitySyncChangeStateRoute route,
        IReadOnlyCollection<string> sourceEntityIds,
        CancellationToken cancellationToken);

    Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken);
}
