using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntityExclusionService(
    IConnectionRuntimeFactory connections,
    IEntityExclusionRepository exclusions)
{
    public async Task<IReadOnlyList<EntityExclusion>> ListAsync(
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(request, cancellationToken).ConfigureAwait(false);
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
        var route = await ResolveRouteAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureExclusionsSupported(route);
        return await exclusions.AddAsync(route, sourceEntityId, sourceName, reason, actor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(
        EntityExclusionRouteRequest request,
        string sourceEntityId,
        string actor,
        CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureExclusionsSupported(route);
        return await exclusions.RevokeAsync(route, sourceEntityId, actor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EntityExclusionRoute> ResolveRouteAsync(
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceVendor = EntitySyncVendors.Normalize(request.SourceVendor);
        var targetVendor = EntitySyncVendors.Normalize(request.TargetVendor);
        var source = await connections.ResolveCurrentDefinitionAsync(
            request.TenantId,
            sourceVendor,
            request.SourceConnectionId,
            cancellationToken).ConfigureAwait(false);
        var target = await connections.ResolveCurrentDefinitionAsync(
            request.TenantId,
            targetVendor,
            request.TargetConnectionId,
            cancellationToken).ConfigureAwait(false);
        return EntityExclusionRoute.Create(
            request.TenantId,
            source.Vendor,
            source.ConnectionId,
            request.SourceEntityType ?? DefaultEntityType(source.Vendor),
            target.Vendor,
            target.ConnectionId,
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
