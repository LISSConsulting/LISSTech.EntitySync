using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IConnectionDefinitionRepository
{
    Task InsertAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        CancellationToken cancellationToken);

    Task<EntitySyncConnectionDefinition?> GetAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
        string tenantId,
        string? vendor,
        bool? enabled,
        CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncConnectionDefinition nextGeneration,
        CancellationToken cancellationToken);
}
