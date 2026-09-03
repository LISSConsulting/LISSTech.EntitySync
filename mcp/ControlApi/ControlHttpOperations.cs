using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public static class ControlHttpOperations
{
    public static Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListConnectionsAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken) =>
        commands.ListConnectionsAsync(control.TenantId, cancellationToken);

    public static Task<DurablePlanCommandResult> CreatePlanAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CreatePlanRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request.LifetimeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(
                nameof(request.LifetimeMinutes),
                "Lifetime must be between 1 and 1440 minutes.");
        return commands.CreatePlanAsync(new CreateDurablePlanRequest
        {
            TenantId = control.TenantId,
            IdempotencyKey = idempotencyKey,
            PolicyId = request.PolicyId,
            PolicyVersion = request.PolicyVersion,
            SourceSearch = request.SourceSearch,
            SourceCount = request.SourceCount,
            SourceEntityId = request.SourceEntityId,
            PlanLifetime = TimeSpan.FromMinutes(request.LifetimeMinutes)
        }, control.Actor, cancellationToken);
    }

    public static Task<DurablePlanCommandResult> CreateShadowPlanAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CreateShadowPlanRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PolicyId == Guid.Empty || request.PolicyVersion <= 0)
            throw new ArgumentException("An exact policy ID and version are required.");
        if (request.LifetimeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(
                nameof(request.LifetimeMinutes),
                "Lifetime must be between 1 and 1440 minutes.");
        if (request.Sources is null || request.Sources.Count is < 1 or > 5000)
            throw new ArgumentException("Canonical shadow sources must contain 1 to 5000 items.");
        var sources = request.Sources.Select(source => source.ToDomain()).ToArray();
        if (sources.Select(source => source.CanonicalEntityId).Distinct().Count()
            != sources.Length)
            throw new ArgumentException("Canonical shadow source IDs must be unique.");
        return commands.CreatePlanAsync(new CreateDurablePlanRequest
        {
            TenantId = control.TenantId,
            IdempotencyKey = idempotencyKey,
            PolicyId = request.PolicyId,
            PolicyVersion = request.PolicyVersion,
            PinnedCanonicalSources = sources,
            PlanLifetime = TimeSpan.FromMinutes(request.LifetimeMinutes)
        }, control.Actor, cancellationToken);
    }

    public static Task<DurablePlanInspectionPage> InspectPlanAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        Guid planId,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        commands.InspectPlanAsync(
            control.TenantId,
            planId,
            page,
            pageSize,
            control.Actor,
            cancellationToken);

    public static Task<DurablePlanApprovalResult> ApprovePlanAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        Guid planId,
        string digest,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        commands.ApprovePlanAsync(
            control.TenantId,
            planId,
            digest,
            idempotencyKey,
            control.Actor,
            cancellationToken);

    public static Task<EntitySyncOperation> QueueDryRunAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        Guid planId,
        string idempotencyKey,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        commands.QueueDryRunAsync(
            control.TenantId,
            planId,
            idempotencyKey,
            correlationId,
            control.Actor,
            cancellationToken);

    public static Task<EntitySyncOperation> QueueApplyAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        Guid planId,
        Guid approvalId,
        string idempotencyKey,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        commands.QueueApplyAsync(
            control.TenantId,
            planId,
            approvalId,
            idempotencyKey,
            correlationId,
            control.Actor,
            cancellationToken);

    public static Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        EntityExclusionRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TenantId.Equals(control.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("The exclusion route tenant does not match the request context.");
        return commands.ListExclusionsAsync(request, cancellationToken);
    }
}
