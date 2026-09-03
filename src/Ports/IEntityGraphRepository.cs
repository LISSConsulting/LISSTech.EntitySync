using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityGraphRepository
{
    Task ObserveEntitiesAsync(EntityGraphObservation observation, CancellationToken cancellationToken);

    Task ObserveRelationshipsAsync(
        IReadOnlyCollection<EntityGraphRelationshipObservation> relationships,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityGraphRecord>> QueryEntitiesAsync(
        EntityGraphQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntityGraphRelationship>> QueryRelationshipsAsync(
        EntityGraphRelationshipQuery query,
        CancellationToken cancellationToken);
}
