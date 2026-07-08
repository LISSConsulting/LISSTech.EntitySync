using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityAdapter
{
    string Vendor { get; }
    IReadOnlyList<string> LookupTypes { get; }
    Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken);
    Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken);
    Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken);
}
