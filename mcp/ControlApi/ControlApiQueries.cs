using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record PlanItemQueryResult(int TotalItems, IReadOnlyList<PlanItemResponse> Items);
public sealed record RunQueryResult(
    DateTimeOffset HighWater,
    IReadOnlyList<RunResponse> Items);
public sealed record AuditQueryResult(
    IReadOnlyList<AuditEventResponse> Events,
    DateTimeOffset? ContinuationOccurredAt,
    Guid? ContinuationEventId);

public interface IControlApiQueries
{
    Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken);
    Task<ConnectionResponse?> GetConnectionAsync(
        string tenantId, string connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PolicyResponse>> ListPoliciesAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken);
    Task<IReadOnlyList<PolicyResponse>> ListPolicyVersionsAsync(
        string tenantId, Guid policyId, int offset, int maximumRows,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PlanResponse>> ListPlansAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken);
    Task<PlanItemQueryResult?> GetPlanItemsAsync(
        string tenantId, Guid planId, int offset, int maximumRows,
        CancellationToken cancellationToken);
    Task<RunQueryResult> ListRunsAsync(
        string tenantId,
        EntitySyncOperationListCursor? cursor,
        int maximumRows,
        CancellationToken cancellationToken);
    Task<RunResponse?> GetRunAsync(
        string tenantId, Guid runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunItemResponse>?> GetRunItemsAsync(
        string tenantId, Guid runId, int offset, int maximumRows,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduleResponse>> ListSchedulesAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken);
    Task<AuditQueryResult> ListAuditAsync(
        string tenantId, DateTimeOffset? occurredAt, Guid? eventId, int maximumRows,
        CancellationToken cancellationToken);
    Task<AuditValuesResponse?> GetAuditValuesAsync(
        string tenantId, Guid eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
        EntityExclusionRouteRequest request, int offset, int maximumRows,
        CancellationToken cancellationToken);
    Task<CapabilityResponse> GetCapabilitiesAsync(
        string tenantId, string connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntityQueryResponse>> GetEntitiesAsync(
        string tenantId, string connectionId, string entityType, string? search,
        bool includeInactive, int maximumRows, CancellationToken cancellationToken);
}

public sealed class ControlApiQueries(
    ConnectionDefinitionService connections,
    ISyncPolicyRepository policies,
    IDurableSyncPlanRepository plans,
    ISyncOperationRepository operations,
    ISyncScheduleRepository schedules,
    ISyncAuditRepository audits,
    IEntitySyncDataProtector protector,
    EntityExclusionService exclusions,
    IConnectionDefinitionRepository connectionDefinitions,
    IConnectionRuntimeFactory runtimes,
    TimeProvider timeProvider) : IControlApiQueries
{
    public async Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken) =>
        (await connections.ListAsync(tenantId, null, null, cancellationToken)
            .ConfigureAwait(false))
        .Skip(offset).Take(maximumRows).Select(ConnectionResponse.From).ToArray();

    public async Task<ConnectionResponse?> GetConnectionAsync(
        string tenantId, string connectionId, CancellationToken cancellationToken)
    {
        try
        {
            return ConnectionResponse.From(await connections.GetAsync(
                tenantId, connectionId, cancellationToken).ConfigureAwait(false));
        }
        catch (ConnectionNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PolicyResponse>> ListPoliciesAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken) =>
        (await policies.ListLatestAsync(tenantId, null, null, cancellationToken)
            .ConfigureAwait(false))
        .Skip(offset).Take(maximumRows).Select(PolicyResponse.From).ToArray();

    public async Task<IReadOnlyList<PolicyResponse>> ListPolicyVersionsAsync(
        string tenantId, Guid policyId, int offset, int maximumRows,
        CancellationToken cancellationToken) =>
        (await policies.ListVersionsAsync(
            tenantId, policyId, offset, maximumRows, cancellationToken)
            .ConfigureAwait(false)).Select(PolicyResponse.From).ToArray();

    public async Task<IReadOnlyList<PlanResponse>> ListPlansAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken) =>
        (await plans.ListAsync(tenantId, offset, maximumRows, cancellationToken)
            .ConfigureAwait(false)).Select(PlanResponse.From).ToArray();

    public async Task<PlanItemQueryResult?> GetPlanItemsAsync(
        string tenantId, Guid planId, int offset, int maximumRows,
        CancellationToken cancellationToken)
    {
        if (await plans.GetAsync(tenantId, planId, cancellationToken).ConfigureAwait(false)
            is null) return null;
        if (offset % maximumRows != 0)
            throw new ArgumentException("The cursor is not aligned to the requested page size.");
        var page = await plans.GetPageAsync(
            tenantId, planId, (offset / maximumRows) + 1, maximumRows, cancellationToken)
            .ConfigureAwait(false);
        return new PlanItemQueryResult(
            page.TotalItems,
            page.Items.Select(PlanItemResponse.From).ToArray());
    }

    public async Task<RunQueryResult> ListRunsAsync(
        string tenantId,
        EntitySyncOperationListCursor? cursor,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        var page = await operations.ListPageAsync(
            tenantId, cursor, maximumRows, cancellationToken).ConfigureAwait(false);
        return new RunQueryResult(
            page.HighWater,
            page.Items.Select(RunResponse.From).ToArray());
    }

    public async Task<RunResponse?> GetRunAsync(
        string tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await operations.GetAsync(tenantId, runId, cancellationToken)
            .ConfigureAwait(false);
        return run is null ? null : RunResponse.From(run);
    }

    public async Task<IReadOnlyList<RunItemResponse>?> GetRunItemsAsync(
        string tenantId, Guid runId, int offset, int maximumRows,
        CancellationToken cancellationToken)
    {
        if (await operations.GetAsync(tenantId, runId, cancellationToken)
            .ConfigureAwait(false) is null) return null;
        return (await operations.GetItemsPageAsync(
            tenantId, runId, offset, maximumRows, cancellationToken)
            .ConfigureAwait(false)).Select(RunItemResponse.From).ToArray();
    }

    public async Task<IReadOnlyList<ScheduleResponse>> ListSchedulesAsync(
        string tenantId, int offset, int maximumRows, CancellationToken cancellationToken) =>
        (await schedules.ListLatestAsync(
            tenantId, offset, maximumRows, cancellationToken).ConfigureAwait(false))
        .Select(ScheduleResponse.From).ToArray();

    public async Task<AuditQueryResult> ListAuditAsync(
        string tenantId, DateTimeOffset? occurredAt, Guid? eventId, int maximumRows,
        CancellationToken cancellationToken)
    {
        var page = await audits.ListAsync(
            tenantId, occurredAt, eventId, maximumRows, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        return new AuditQueryResult(
            page.Events.Select(value => AuditEventResponse.From(value, now)).ToArray(),
            page.ContinuationOccurredAt,
            page.ContinuationEventId);
    }

    public async Task<AuditValuesResponse?> GetAuditValuesAsync(
        string tenantId, Guid eventId, CancellationToken cancellationToken)
    {
        var retained = await audits.GetFullValuesAsync(
            tenantId, eventId, cancellationToken).ConfigureAwait(false);
        if (retained is null) return null;
        return new AuditValuesResponse(
            eventId,
            protector.Unprotect(
                EntitySyncDataProtectionPurpose.AuditValue,
                retained.FullValuesCiphertext),
            retained.ExpiresAt);
    }

    public async Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
        EntityExclusionRouteRequest request, int offset, int maximumRows,
        CancellationToken cancellationToken) =>
        (await exclusions.ListAsync(request, cancellationToken).ConfigureAwait(false))
        .Skip(offset).Take(maximumRows).ToArray();

    public async Task<CapabilityResponse> GetCapabilitiesAsync(
        string tenantId, string connectionId, CancellationToken cancellationToken)
    {
        var definition = await RequireDefinitionAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var capabilities = await lease.Adapter.GetCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            return new CapabilityResponse(
                definition.ConnectionId,
                capabilities.Vendor,
                capabilities.EntityTypes.Select(entity => new CapabilityEntityResponse(
                    entity.EntityType,
                    entity.SupportedActions.Order(StringComparer.Ordinal).ToArray(),
                    entity.SupportedFields.Order(StringComparer.Ordinal).ToArray())).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The entity adapter is unavailable.", exception);
        }
    }

    public async Task<IReadOnlyList<EntityQueryResponse>> GetEntitiesAsync(
        string tenantId, string connectionId, string entityType, string? search,
        bool includeInactive, int maximumRows, CancellationToken cancellationToken)
    {
        var definition = await RequireDefinitionAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entities = await lease.Adapter.GetEntitiesAsync(new EntityQuery
            {
                EntityType = entityType,
                Search = search,
                IncludeInactive = includeInactive,
                FullObjects = false,
                Count = maximumRows
            }, cancellationToken).ConfigureAwait(false);
            return entities.Take(maximumRows).Select(entity => new EntityQueryResponse(
                entity.Vendor,
                entity.EntityType,
                entity.Id,
                entity.Name,
                entity.Email,
                entity.Phone,
                entity.Website,
                entity.IsActive)).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The entity adapter is unavailable.", exception);
        }
    }

    private async Task<EntitySyncConnectionDefinition> RequireDefinitionAsync(
        string tenantId, string connectionId, CancellationToken cancellationToken) =>
        await connectionDefinitions.GetAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false)
        ?? throw new ConnectionNotFoundException(tenantId, connectionId);
}
