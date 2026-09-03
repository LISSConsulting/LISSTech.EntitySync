using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface ISyncPolicyRepository
{
    Task InsertAsync(
        string tenantId,
        EntitySyncPolicy policy,
        CancellationToken cancellationToken);

    Task<bool> TryInsertValidatedAsync(
        string tenantId,
        EntitySyncPolicy policy,
        string sourceConnectionId,
        long sourceGeneration,
        string targetConnectionId,
        long targetGeneration,
        CancellationToken cancellationToken);

    Task<bool> TryInsertValidatedWithTokenAsync(
        string tenantId,
        EntitySyncPolicy policy,
        string sourceConnectionId,
        long sourceGeneration,
        string targetConnectionId,
        long targetGeneration,
        string idempotencyToken,
        CancellationToken cancellationToken);

    Task<EntitySyncPolicy?> GetByIdempotencyTokenAsync(
        string tenantId,
        Guid policyId,
        string idempotencyToken,
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

    Task<IReadOnlyList<EntitySyncPolicy>> ListVersionsAsync(
        string tenantId,
        Guid policyId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntitySyncPolicy>>([]);
}
