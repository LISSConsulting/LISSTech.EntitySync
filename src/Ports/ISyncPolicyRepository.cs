using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncPolicyRepository
{
    Task InsertAsync(
        string tenantId,
        EntitySyncPolicy policy,
        CancellationToken cancellationToken);

    Task<EntitySyncPolicy?> GetAsync(
        string tenantId,
        Guid policyId,
        int version,
        CancellationToken cancellationToken);

    Task<EntitySyncPolicy?> GetLatestAsync(
        string tenantId,
        Guid policyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(
        string tenantId,
        string? routeScope,
        bool? enabled,
        CancellationToken cancellationToken);
}
