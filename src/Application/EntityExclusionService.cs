using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntityExclusionService(
    IEntityConnectionRepository connections,
    IEntityExclusionRepository exclusions)
{
    public async Task<IReadOnlyList<EntityExclusion>> ListAsync(
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(request);
        return await exclusions.ListActiveAsync(route, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntityExclusion> AddAsync(
        EntityExclusionRouteRequest request,
        string sourceEntityId,
        string sourceName,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(request);
        EnsureExclusionsSupported(route);
        return await exclusions.AddAsync(route, sourceEntityId, sourceName, reason, actor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(
        EntityExclusionRouteRequest request,
        string sourceEntityId,
        string actor,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(request);
        EnsureExclusionsSupported(route);
        return await exclusions.RevokeAsync(route, sourceEntityId, actor, cancellationToken).ConfigureAwait(false);
    }

    private EntityExclusionRoute ResolveRoute(EntityExclusionRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceVendor = EntitySyncVendors.Normalize(request.SourceVendor);
        var targetVendor = EntitySyncVendors.Normalize(request.TargetVendor);
        var source = connections.Resolve(request.TenantId, sourceVendor, request.SourceConnectionId);
        var target = connections.Resolve(request.TenantId, targetVendor, request.TargetConnectionId);
        return EntityExclusionRoute.Create(
            request.TenantId,
            source.Vendor,
            source.Id,
            request.SourceEntityType ?? DefaultEntityType(source.Vendor),
            target.Vendor,
            target.Id,
            request.TargetEntityType ?? DefaultEntityType(target.Vendor));
    }

    private static void EnsureExclusionsSupported(EntityExclusionRoute route)
    {
        if (EntitySyncVendors.IsAgentController(route.TargetVendor))
            throw new InvalidOperationException("AgentController authoritative customer-scope synchronization does not permit exclusions; omission could retire an existing scope.");
    }

    private static string DefaultEntityType(string vendor) => vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) || EntitySyncVendors.IsBillCom(vendor) ? "Client" : "Customer";
}

public sealed class EntityExclusionRouteRequest
{
    public string TenantId { get; init; } = string.Empty;
    public string SourceVendor { get; init; } = string.Empty;
    public string? SourceConnectionId { get; init; }
    public string? SourceEntityType { get; init; }
    public string TargetVendor { get; init; } = string.Empty;
    public string? TargetConnectionId { get; init; }
    public string? TargetEntityType { get; init; }
}
