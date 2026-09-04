using System.Security.Cryptography;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public static class ControlEndpoints
{
    private const string Prefix = "/api/v1/control";
    private const string CorrelationHeaderName = "X-Correlation-ID";

    public static WebApplication UseControlApiErrors(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (context.Response.HasStarted)
                {
                    context.Abort();
                    return;
                }
                var (status, code, detail) = MapException(exception);
                await ControlProblem.Create(context, status, code, detail)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
        });
        return app;
    }

    public static WebApplication MapControlApi(this WebApplication app)
    {
        var group = app.MapGroup(Prefix);

        Read(group.MapGet("/connections", ListConnectionsAsync),
            "ListControlConnections").Produces<ControlPage<ConnectionResponse>>();
        Read(group.MapGet("/connections/{connectionId}", GetConnectionAsync),
            "GetControlConnection").Produces<ConnectionResponse>();
        Mutate(group.MapPost("/connections", CreateConnectionAsync),
            "CreateControlConnection", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ConnectionResponse>(StatusCodes.Status201Created);
        Mutate(group.MapPatch("/connections/{connectionId}", UpdateConnectionAsync),
            "UpdateControlConnection", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ConnectionResponse>();
        Mutate(group.MapDelete("/connections/{connectionId}", DeleteConnectionAsync),
            "DeleteControlConnection", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ConnectionDeleteResponse>();
        Mutate(group.MapPost("/connections/{connectionId}/test", TestConnectionAsync),
            "TestControlConnection", ControlPolicies.Manage)
            .Produces<ConnectionTestResponse>();

        Read(group.MapGet("/policies", ListPoliciesAsync),
            "ListControlPolicies").Produces<ControlPage<PolicyResponse>>();
        Mutate(group.MapPost("/policies", CreatePolicyAsync),
            "CreateControlPolicy", ControlPolicies.Manage)
            .Produces<PolicyResponse>(StatusCodes.Status201Created);
        Read(group.MapGet("/policies/{policyId:guid}/versions", ListPolicyVersionsAsync),
            "ListControlPolicyVersions").Produces<ControlPage<PolicyResponse>>();
        Mutate(group.MapPost("/policies/{policyId:guid}/versions", CreatePolicyVersionAsync),
            "CreateControlPolicyVersion", ControlPolicies.Manage)
            .Produces<PolicyResponse>(StatusCodes.Status201Created);

        Read(group.MapGet("/plans", ListPlansAsync),
            "ListControlPlans").Produces<ControlPage<PlanResponse>>();
        Mutate(group.MapPost("/plans", CreatePlanAsync),
            "CreateControlPlan", ControlPolicies.Operate)
            .Produces<PlanResponse>(StatusCodes.Status201Created);
        Mutate(group.MapPost("/plans/shadow-projection", CreateShadowPlanAsync),
            "CreateControlShadowPlan", ControlPolicies.Operate)
            .Produces<PlanResponse>(StatusCodes.Status201Created);
        Read(group.MapGet("/plans/{planId:guid}/items", GetPlanItemsAsync),
            "ListControlPlanItems").Produces<ControlPage<PlanItemResponse>>();
        Mutate(group.MapPost("/plans/{planId:guid}/inspections", InspectPlanAsync),
            "InspectControlPlan", ControlPolicies.Operate)
            .Produces<InspectionResponse>();
        Mutate(group.MapPost("/plans/{planId:guid}/approvals", ApprovePlanAsync),
            "ApproveControlPlan", ControlPolicies.Approve)
            .Produces<ApprovalResponse>();
        Mutate(group.MapPost("/plans/{planId:guid}/dry-run", QueueDryRunAsync),
            "DryRunControlPlan", ControlPolicies.Operate)
            .Produces<QueuedRunResponse>(StatusCodes.Status202Accepted);
        Mutate(group.MapPost("/plans/{planId:guid}/apply", QueueApplyAsync),
            "ApplyControlPlan", ControlPolicies.Approve)
            .Produces<QueuedRunResponse>(StatusCodes.Status202Accepted);

        Read(group.MapGet("/runs", ListRunsAsync),
            "ListControlRuns").Produces<RunPageResponse>();
        Read(group.MapGet("/runs/{runId:guid}", GetRunAsync),
            "GetControlRun").Produces<RunResponse>();
        Read(group.MapGet("/runs/{runId:guid}/items", GetRunItemsAsync),
            "ListControlRunItems").Produces<ControlPage<RunItemResponse>>();

        Read(group.MapGet("/schedules", ListSchedulesAsync),
            "ListControlSchedules").Produces<ControlPage<ScheduleResponse>>();
        Document(group.MapPost("/schedules/preview", PreviewSchedule),
            "PreviewControlSchedule", ControlPolicies.Manage)
            .Produces<PreviewScheduleResponse>();
        Mutate(group.MapPost("/schedules", CreateScheduleAsync),
            "CreateControlSchedule", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ScheduleResponse>(StatusCodes.Status201Created);
        Mutate(group.MapPost("/schedules/{scheduleId:guid}/versions", CreateScheduleVersionAsync),
            "CreateControlScheduleVersion", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ScheduleResponse>(StatusCodes.Status201Created);
        Mutate(group.MapPost("/schedules/{scheduleId:guid}/runs", QueueScheduleRunAsync),
            "QueueControlScheduleRun", ControlPolicies.Operate,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<QueuedScheduleRunResponse>(StatusCodes.Status202Accepted);

        Read(group.MapGet("/audit", ListAuditAsync),
            "ListControlAudit").Produces<ControlPage<AuditEventResponse>>();
        Document(group.MapGet("/audit/{eventId:guid}/values", GetAuditValuesAsync),
            "GetControlAuditValues", ControlPolicies.Audit)
            .Produces<AuditValuesResponse>();

        Read(group.MapGet("/exclusions", ListExclusionsAsync),
            "ListControlExclusions").Produces<ControlPage<ExclusionResponse>>();
        Mutate(group.MapPost("/exclusions", CreateExclusionAsync),
            "CreateControlExclusion", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<ExclusionResponse>(StatusCodes.Status201Created);
        Mutate(group.MapDelete("/exclusions", DeleteExclusionAsync),
            "DeleteControlExclusion", ControlPolicies.Manage,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces(StatusCodes.Status204NoContent);

        Read(group.MapGet("/capabilities", GetCapabilitiesAsync),
            "GetControlCapabilities").Produces<CapabilityResponse>();
        Read(group.MapGet("/vendors", ListVendorsAsync),
            "ListControlVendors").Produces<VendorCatalogResponse>();
        Read(group.MapGet("/entities", GetEntitiesAsync),
            "ListControlEntities").Produces<ControlPage<EntityQueryResponse>>();
        Read(group.MapGet(
                "/connections/{connectionId}/entity-refreshes",
                ListEntityRefreshesAsync),
            "ListEntityRefreshes")
            .Produces<EntityRefreshListResponse>();
        Mutate(group.MapPost(
                "/connections/{connectionId}/entity-refreshes",
                QueueEntityRefreshAsync),
            "QueueEntityRefresh", ControlPolicies.Operate,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<EntityRefreshResponse>(StatusCodes.Status202Accepted);
        Mutate(group.MapPost(
                "/connections/{connectionId}/entity-events",
                AcceptAtomicEntityEventAsync),
            "AcceptAtomicEntityEvent", ControlPolicies.CanonicalChanges,
            IdempotencyExecutionMode.AtomicDatabase)
            .Produces<AtomicEntityEventResponse>(StatusCodes.Status202Accepted);
        Mutate(group.MapPost("/canonical-changes", AcceptCanonicalChangeAsync),
            "AcceptCanonicalChange", ControlPolicies.CanonicalChanges)
            .Produces<CanonicalChangeIntakeResponse>(StatusCodes.Status202Accepted);
        Mutate(group.MapPost("/expert/suiteql", ExecuteSuiteQlAsync),
            "ExecuteControlSuiteQl", ControlPolicies.Expert)
            .Produces<SuiteQlResponse>();
        Mutate(group.MapPost("/expert/custom-properties", SetCustomPropertyAsync),
            "SetControlCustomProperty", ControlPolicies.Expert)
            .Produces<CustomPropertyResponse>();

        return app;
    }

    private static RouteHandlerBuilder Read(RouteHandlerBuilder endpoint, string name) =>
        Document(endpoint, name, ControlPolicies.Read);

    private static RouteHandlerBuilder Document(
        RouteHandlerBuilder endpoint,
        string name,
        string policy) =>
        endpoint.WithName(name).RequireAuthorization(policy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static RouteHandlerBuilder Mutate(
        RouteHandlerBuilder endpoint,
        string name,
        string policy,
        IdempotencyExecutionMode mode = IdempotencyExecutionMode.Recoverable) =>
        Document(endpoint, name, policy)
            .WithMetadata(new IdempotencyExecutionMetadata(mode))
            .AddEndpointFilter<IdempotencyEndpointFilter>();

    private static async Task<IResult> ListConnectionsAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var offset = Offset(cursors, cursor, "connections", control, pageSize);
        var all = await ControlHttpOperations.ListConnectionsAsync(
            commands, control, cancellationToken).ConfigureAwait(false);
        var values = all.Skip(offset).Take(pageSize + 1)
            .Select(ConnectionResponse.From).ToArray();
        return Page(values, pageSize, offset, cursors, "connections", control);
    }

    private static async Task<IResult> GetConnectionAsync(
        string connectionId,
        IControlApiQueries queries,
        ControlRequestContext control,
        CancellationToken cancellationToken) =>
        await queries.GetConnectionAsync(
            control.TenantId, connectionId, cancellationToken).ConfigureAwait(false)
        is { } value
            ? Results.Ok(value)
            : throw new KeyNotFoundException("The connection was not found.");

    private static async Task<IResult> CreateConnectionAsync(
        HttpContext http,
        CreateConnectionRequest request,
        ConnectionDefinitionService service,
        IServerManagedEntityAdapterFactory factory,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? secrets = null;
        try
        {
            var configuration = factory.GetConnectionConfiguration(request.Vendor, null);
            secrets = configuration.SecretConfiguration;
            var generatedId = string.IsNullOrWhiteSpace(request.ConnectionId)
                ? EntityRefreshService.GenerateConnectionId()
                : request.ConnectionId.Trim();
            var created = await service.CreateAsync(
                control.TenantId,
                new ConnectionDefinitionRequest(
                    request.Vendor,
                    generatedId,
                    request.DisplayName,
                    configuration.PublicConfiguration,
                    configuration.SecretConfiguration,
                    request.PlatformInstanceId ?? configuration.PlatformInstanceId),
                control.Actor,
                cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ConnectionResponse.From(created), statusCode: StatusCodes.Status201Created);
        }
        finally
        {
            if (secrets is IDictionary<string, string> mutable) mutable.Clear();
        }
    }

    private static async Task<IResult> UpdateConnectionAsync(
        string connectionId,
        UpdateConnectionRequest request,
        ConnectionDefinitionService service,
        IServerManagedEntityAdapterFactory factory,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var current = await service.GetAsync(
            control.TenantId, connectionId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string>? secrets = null;
        try
        {
            var configuration = factory.GetConnectionConfiguration(current.Vendor, null);
            secrets = configuration.SecretConfiguration;
            var updated = await service.UpdateAsync(
                control.TenantId,
                connectionId,
                request.ExpectedGeneration,
                new ConnectionDefinitionRequest(
                    current.Vendor,
                    connectionId,
                    request.DisplayName,
                    configuration.PublicConfiguration,
                    configuration.SecretConfiguration,
                    request.PlatformInstanceId
                    ?? current.PlatformInstanceId
                    ?? configuration.PlatformInstanceId),
                control.Actor,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ConnectionResponse.From(updated));
        }
        finally
        {
            if (secrets is IDictionary<string, string> mutable) mutable.Clear();
        }
    }

    private static async Task<IResult> DeleteConnectionAsync(
        string connectionId,
        [FromBody] DeleteConnectionRequest request,
        ConnectionDefinitionService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(
            control.TenantId,
            connectionId,
            request.ExpectedGeneration,
            control.Actor,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ConnectionDeleteResponse(
            connectionId,
            result.Outcome.ToString(),
            result.Definition?.Generation));
    }

    private static async Task<IResult> TestConnectionAsync(
        HttpContext http,
        string connectionId,
        TestConnectionRequest request,
        ConnectionDefinitionService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var connected = await service.TestAsync(
            control.TenantId,
            connectionId,
            request.ExpectedGeneration,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ConnectionTestResponse(
            connectionId, request.ExpectedGeneration, connected, http.TraceIdentifier));
    }

    private static async Task<IResult> ListPoliciesAsync(
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var offset = Offset(cursors, cursor, "policies", control, pageSize);
        var values = await queries.ListPoliciesAsync(
            control.TenantId, offset, pageSize + 1, cancellationToken).ConfigureAwait(false);
        return Page(values, pageSize, offset, cursors, "policies", control);
    }

    private static async Task<IResult> CreatePolicyAsync(
        HttpContext http,
        CreatePolicyRequest request,
        SyncPolicyService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var executionToken = IdempotencyEndpointFilter.GetExecutionToken(http);
        var policyId = StableGuid(executionToken);
        if (IdempotencyEndpointFilter.IsRecovery(http))
        {
            var recovered = await service.GetByIdempotencyTokenAsync(
                control.TenantId, policyId, executionToken, cancellationToken)
                .ConfigureAwait(false);
            if (recovered is not null)
                return Results.Json(
                    PolicyResponse.From(recovered),
                    statusCode: StatusCodes.Status201Created);
        }
        var value = await service.CreateIdempotentAsync(
            control.TenantId,
            policyId,
            new SyncPolicyRequest(
                request.Name, request.RouteScope, request.Definition.ToDomain(), request.Enabled),
            control.Actor,
            executionToken,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(PolicyResponse.From(value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListPolicyVersionsAsync(
        Guid policyId,
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var resource = $"policy-versions:{policyId:N}";
        var offset = Offset(cursors, cursor, resource, control, pageSize);
        var values = await queries.ListPolicyVersionsAsync(
            control.TenantId, policyId, offset, pageSize + 1, cancellationToken)
            .ConfigureAwait(false);
        return Page(values, pageSize, offset, cursors, resource, control);
    }

    private static async Task<IResult> CreatePolicyVersionAsync(
        HttpContext http,
        Guid policyId,
        CreatePolicyVersionRequest request,
        SyncPolicyService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var executionToken = IdempotencyEndpointFilter.GetExecutionToken(http);
        if (IdempotencyEndpointFilter.IsRecovery(http))
        {
            var recovered = await service.GetByIdempotencyTokenAsync(
                control.TenantId, policyId, executionToken, cancellationToken)
                .ConfigureAwait(false);
            if (recovered is not null)
                return Results.Json(
                    PolicyResponse.From(recovered),
                    statusCode: StatusCodes.Status201Created);
        }
        var value = await service.CreateNextVersionIdempotentAsync(
            control.TenantId,
            policyId,
            request.ExpectedVersion,
            request.Definition.ToDomain(),
            request.Enabled,
            control.Actor,
            executionToken,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(PolicyResponse.From(value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListPlansAsync(
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var offset = Offset(cursors, cursor, "plans", control, pageSize);
        var values = await queries.ListPlansAsync(
            control.TenantId, offset, pageSize + 1, cancellationToken).ConfigureAwait(false);
        return Page(values, pageSize, offset, cursors, "plans", control);
    }

    private static async Task<IResult> CreatePlanAsync(
        HttpContext http,
        CreatePlanRequest request,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var command = await ControlHttpOperations.CreatePlanAsync(
            commands,
            control,
            request,
            IdempotencyEndpointFilter.GetCallerKey(http),
            cancellationToken).ConfigureAwait(false);
        var persisted = command.Plan;
        return Results.Json(PlanResponse.From(persisted), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> CreateShadowPlanAsync(
        HttpContext http,
        CreateShadowPlanRequest request,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var command = await ControlHttpOperations.CreateShadowPlanAsync(
            commands,
            control,
            request,
            IdempotencyEndpointFilter.GetCallerKey(http),
            cancellationToken).ConfigureAwait(false);
        return Results.Json(
            PlanResponse.From(command.Plan),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetPlanItemsAsync(
        Guid planId,
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var resource = $"plan-items:{planId:N}";
        var offset = Offset(cursors, cursor, resource, control, pageSize);
        var result = await queries.GetPlanItemsAsync(
            control.TenantId, planId, offset, pageSize, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The plan was not found.");
        var next = offset + result.Items.Count < result.TotalItems
            ? cursors.ProtectOffset(resource, control.TenantId, offset + result.Items.Count)
            : null;
        return Results.Ok(new ControlPage<PlanItemResponse>(result.Items, next));
    }

    private static async Task<IResult> InspectPlanAsync(
        Guid planId,
        InspectPlanRequest request,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        CancellationToken cancellationToken)
    {
        ValidatePageSize(request.PageSize);
        var resource = $"plan-inspection:{planId:N}";
        var offset = string.IsNullOrWhiteSpace(request.Cursor)
            ? 0
            : cursors.UnprotectOffset(request.Cursor, resource, control.TenantId);
        if (offset % request.PageSize != 0) throw new InvalidControlCursorException();
        var page = await ControlHttpOperations.InspectPlanAsync(
            commands,
            control,
            planId,
            (offset / request.PageSize) + 1,
            request.PageSize,
            cancellationToken).ConfigureAwait(false);
        var next = offset + page.Items.Count < page.Plan.ItemCount
            ? cursors.ProtectOffset(resource, control.TenantId, offset + page.Items.Count)
            : null;
        return Results.Ok(new InspectionResponse(
            planId,
            page.InspectionId,
            page.Plan.Digest,
            page.CoveredItemCount,
            page.InspectionComplete,
            page.Items.Select(PlanItemResponse.From).ToArray(),
            next));
    }

    private static async Task<IResult> ApprovePlanAsync(
        HttpContext http,
        Guid planId,
        ApprovePlanRequest request,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var approval = await ControlHttpOperations.ApprovePlanAsync(
            commands,
            control,
            planId,
            request.Digest,
            IdempotencyEndpointFilter.GetCallerKey(http),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ApprovalResponse(
            approval.PlanId,
            approval.ApprovalId,
            approval.InspectionId,
            approval.Digest,
            approval.ApprovedAt,
            approval.ExpiresAt));
    }

    private static async Task<IResult> QueueDryRunAsync(
        HttpContext http,
        Guid planId,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var operation = await ControlHttpOperations.QueueDryRunAsync(
            commands,
            control,
            planId,
            IdempotencyEndpointFilter.GetCallerKey(http),
            RequireCorrelationId(http),
            cancellationToken).ConfigureAwait(false);
        return Results.Json(new QueuedRunResponse(
            operation.OperationId,
            planId,
            operation.Mode.ToString(),
            operation.Status.ToString(),
            operation.CorrelationId?.ToString("D")
            ?? throw new InvalidOperationException(
                "Queued operation has no correlation identity.")),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> QueueApplyAsync(
        HttpContext http,
        Guid planId,
        ApplyPlanRequest request,
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var operation = await ControlHttpOperations.QueueApplyAsync(
            commands,
            control,
            planId,
            request.ApprovalId,
            IdempotencyEndpointFilter.GetCallerKey(http),
            RequireCorrelationId(http),
            cancellationToken).ConfigureAwait(false);
        return Results.Json(new QueuedRunResponse(
            operation.OperationId,
            planId,
            operation.Mode.ToString(),
            operation.Status.ToString(),
            operation.CorrelationId?.ToString("D")
            ?? throw new InvalidOperationException(
                "Queued operation has no correlation identity.")),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListRunsAsync(
        HttpContext http,
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is not null) ValidatePageSize(pageSize.Value);
        if (http.Request.Query.ContainsKey("offset"))
            throw new BadHttpRequestException("Offset pagination is not supported.");
        EntitySyncOperationListCursor? position = null;
        var effectivePageSize = pageSize ?? 25;
        var hasCursor = cursor is not null;
        if (hasCursor)
        {
            var decoded = cursors.UnprotectRun(cursor!, "runs", control.TenantId);
            if (pageSize is not null && pageSize.Value != decoded.PageSize)
                throw new InvalidControlCursorException();
            effectivePageSize = decoded.PageSize;
            position = new EntitySyncOperationListCursor(
                decoded.HighWater,
                decoded.LastQueuedAt,
                decoded.LastOperationId);
        }
        var page = await queries.ListRunsAsync(
            control.TenantId, position, effectivePageSize + 1, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<RunResponse> items = page.Items.Count > effectivePageSize
            ? page.Items.Take(effectivePageSize).ToArray()
            : page.Items;
        string? next = null;
        if (page.Items.Count > effectivePageSize)
        {
            var last = items[^1];
            next = cursors.ProtectRun(
                "runs",
                control.TenantId,
                page.HighWater,
                last.QueuedAt,
                last.RunId,
                effectivePageSize);
        }
        var replay = hasCursor
            ? cursor!
            : cursors.ProtectRunStart(
                "runs", control.TenantId, page.HighWater, effectivePageSize);
        return Results.Ok(new RunPageResponse(items, replay, next));
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        IControlApiQueries queries,
        ControlRequestContext control,
        CancellationToken cancellationToken) =>
        await queries.GetRunAsync(control.TenantId, runId, cancellationToken)
            .ConfigureAwait(false)
        is { } value
            ? Results.Ok(value)
            : throw new KeyNotFoundException("The run was not found.");

    private static async Task<IResult> GetRunItemsAsync(
        Guid runId,
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var resource = $"run-items:{runId:N}";
        var offset = Offset(cursors, cursor, resource, control, pageSize);
        var values = await queries.GetRunItemsAsync(
            control.TenantId, runId, offset, pageSize + 1, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The run was not found.");
        return Page(values, pageSize, offset, cursors, resource, control);
    }

    private static async Task<IResult> ListSchedulesAsync(
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var offset = Offset(cursors, cursor, "schedules", control, pageSize);
        var values = await queries.ListSchedulesAsync(
            control.TenantId, offset, pageSize + 1, cancellationToken).ConfigureAwait(false);
        return Page(values, pageSize, offset, cursors, "schedules", control);
    }

    private static IResult PreviewSchedule(
        PreviewScheduleRequest request,
        SyncScheduleService service,
        TimeProvider timeProvider)
    {
        var occurrences = service.PreviewOccurrences(
            request.CronExpression,
            request.TimeZone,
            timeProvider.GetUtcNow());
        return Results.Ok(new PreviewScheduleResponse(occurrences));
    }

    private static async Task<IResult> CreateScheduleAsync(
        HttpContext http,
        CreateScheduleRequest request,
        SyncScheduleService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var scheduleId = request.ScheduleId is null || request.ScheduleId == Guid.Empty
            ? StableGuid(IdempotencyEndpointFilter.GetExecutionToken(http))
            : request.ScheduleId.Value;
        var value = await service.CreateAsync(
            control.TenantId,
            scheduleId,
            ToScheduleRequest(request),
            control.Actor,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(ScheduleResponse.From(value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> QueueScheduleRunAsync(
        HttpContext http,
        Guid scheduleId,
        QueueScheduleRunRequest request,
        SyncScheduleRunService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var value = await service.QueueNowAsync(
            control.TenantId,
            scheduleId,
            request.ExpectedVersion,
            StableGuid(IdempotencyEndpointFilter.GetExecutionToken(http)),
            control.Actor,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(
            QueuedScheduleRunResponse.From(value),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> CreateScheduleVersionAsync(
        Guid scheduleId,
        CreateScheduleVersionRequest request,
        SyncScheduleService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var value = await service.CreateNextVersionAsync(
            control.TenantId,
            scheduleId,
            request.ExpectedVersion,
            ToScheduleRequest(request),
            control.Actor,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(ScheduleResponse.From(value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAuditAsync(
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        ValidatePageSize(pageSize);
        DateTimeOffset? occurredAt = null;
        Guid? eventId = null;
        if (!string.IsNullOrWhiteSpace(cursor))
            (occurredAt, eventId) = cursors.UnprotectAudit(
                cursor, "audit", control.TenantId);
        var result = await queries.ListAuditAsync(
            control.TenantId, occurredAt, eventId, pageSize, cancellationToken)
            .ConfigureAwait(false);
        var next = result.ContinuationOccurredAt is not null
                   && result.ContinuationEventId is not null
            ? cursors.ProtectAudit(
                "audit",
                control.TenantId,
                result.ContinuationOccurredAt.Value,
                result.ContinuationEventId.Value)
            : null;
        return Results.Ok(new ControlPage<AuditEventResponse>(result.Events, next));
    }

    private static async Task<IResult> GetAuditValuesAsync(
        Guid eventId,
        IControlApiQueries queries,
        ControlRequestContext control,
        CancellationToken cancellationToken) =>
        await queries.GetAuditValuesAsync(control.TenantId, eventId, cancellationToken)
            .ConfigureAwait(false)
        is { } value
            ? Results.Ok(value)
            : throw new KeyNotFoundException("The retained audit values were not found.");

    private static async Task<IResult> ListExclusionsAsync(
        IEntitySyncControlCommands commands,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string sourceVendor,
        string targetVendor,
        string? sourceConnectionId,
        string? sourceEntityType,
        string? targetConnectionId,
        string? targetEntityType,
        string? cursor,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var resource = $"exclusions:{sourceVendor}:{sourceConnectionId}:{sourceEntityType}:" +
                       $"{targetVendor}:{targetConnectionId}:{targetEntityType}";
        var offset = Offset(cursors, cursor, resource, control, pageSize);
        var all = await ControlHttpOperations.ListExclusionsAsync(
            commands,
            control,
            Route(control, new ExclusionRouteContract(
                sourceVendor, sourceConnectionId, sourceEntityType,
                targetVendor, targetConnectionId, targetEntityType)),
            cancellationToken).ConfigureAwait(false);
        var values = all.Skip(offset).Take(pageSize + 1).ToArray();
        return Page(
            values.Select(ExclusionResponse.From).ToArray(),
            pageSize,
            offset,
            cursors,
            resource,
            control);
    }

    private static async Task<IResult> CreateExclusionAsync(
        CreateExclusionRequest request,
        EntityExclusionService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var value = await service.AddAsync(
            Route(control, request.Route),
            request.SourceEntityId,
            request.SourceName,
            request.Reason,
            control.ActorId,
            cancellationToken).ConfigureAwait(false);
        return Results.Json(ExclusionResponse.From(value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteExclusionAsync(
        [FromBody] DeleteExclusionRequest request,
        EntityExclusionService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        if (!await service.RevokeAsync(
                Route(control, request.Route),
                request.SourceEntityId,
                control.ActorId,
                cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("The active exclusion was not found.");
        return Results.NoContent();
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        string connectionId,
        IControlApiQueries queries,
        ControlRequestContext control,
        CancellationToken cancellationToken) =>
        Results.Ok(await queries.GetCapabilitiesAsync(
            control.TenantId, connectionId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetEntitiesAsync(
        string connectionId,
        string entityType,
        IControlApiQueries queries,
        ControlRequestContext control,
        ControlCursorProtector cursors,
        string? search,
        bool includeInactive = false,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var resource = $"entities:{connectionId}:{entityType}:{search}:{includeInactive}";
        var offset = Offset(cursors, cursor, resource, control, pageSize);
        // Page semantics: fetch pageSize + 1; if we got more than pageSize rows, there
        // is a next page and we drop the sentinel row before returning the slice.
        var values = await queries.GetEntitiesAsync(
            control.TenantId,
            connectionId,
            entityType,
            search,
            includeInactive,
            offset,
            pageSize + 1,
            cancellationToken).ConfigureAwait(false);
        var hasMore = values.Count > pageSize;
        var page = hasMore ? values.Take(pageSize).ToArray() : values;
        var next = hasMore
            ? cursors.ProtectOffset(resource, control.TenantId, offset + page.Count)
            : null;
        return Results.Ok(new ControlPage<EntityQueryResponse>(page, next));
    }
    private static async Task<IResult> AcceptCanonicalChangeAsync(
        HttpContext http,
        CanonicalChangeIntakeRequest request,
        CanonicalChangeService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var receipt = await service.AcceptAsync(new CanonicalChangeRequest(
            control.TenantId,
            request.OutboxEventId,
            request.CanonicalEntityType,
            request.CanonicalEntityId,
            request.CanonicalVersion,
            request.ChangedFields,
            new EntitySyncSha256(request.PayloadSha256),
            request.OccurredAt), cancellationToken).ConfigureAwait(false);
        return Results.Json(new CanonicalChangeIntakeResponse(
            receipt.ReceiptId,
            receipt.OutboxEventId,
            receipt.CanonicalEntityId,
            receipt.CanonicalVersion,
            receipt.PayloadSha256.Value,
            receipt.WorkIds,
            receipt.ReceivedAt,
            http.TraceIdentifier), statusCode: StatusCodes.Status202Accepted);
    }
    private static async Task<IResult> ExecuteSuiteQlAsync(
        HttpContext http,
        SuiteQlRequest request,
        ExpertOperationService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteSuiteQlAsync(
            control.TenantId,
            request.ConnectionId,
            request.Query,
            request.MaximumRows,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new SuiteQlResponse(
            request.ConnectionId,
            result.Rows.Count,
            result.Rows,
            result.Truncated,
            http.TraceIdentifier));
    }

    private static async Task<IResult> SetCustomPropertyAsync(
        HttpContext http,
        CustomPropertyRequest request,
        ExpertOperationService service,
        ControlRequestContext control,
        CancellationToken cancellationToken)
    {
        var correlationId = IdempotencyEndpointFilter.GetExecutionToken(http);
        if (IdempotencyEndpointFilter.IsRecovery(http))
        {
            var readback = await service.GetCustomPropertyAsync(
                control.TenantId,
                request.ConnectionId,
                request.EntityId,
                request.Name,
                cancellationToken).ConfigureAwait(false);
            if (!readback.Found
                || !string.Equals(readback.Value, request.Value, StringComparison.Ordinal))
                throw new EntitySyncIdempotencyRecoveryUnknownException(
                    "The external custom-property outcome cannot be recovered exactly.");
            return Results.Ok(new CustomPropertyResponse(
                request.ConnectionId,
                request.EntityId,
                request.Name,
                true,
                "RECOVERED_BY_READBACK",
                correlationId));
        }

        var result = await service.SetCustomPropertyAsync(
            control.TenantId,
            request.ConnectionId,
            request.EntityId,
            request.Name,
            request.Value,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new CustomPropertyResponse(
            request.ConnectionId,
            request.EntityId,
            request.Name,
            result.Success,
            result.SafeCode,
            correlationId));
    }

    private static EntityExclusionRouteRequest Route(
        ControlRequestContext control,
        ExclusionRouteContract route) => new()
        {
            TenantId = control.TenantId,
            SourceVendor = route.SourceVendor,
            SourceConnectionId = route.SourceConnectionId,
            SourceEntityType = route.SourceEntityType,
            TargetVendor = route.TargetVendor,
            TargetConnectionId = route.TargetConnectionId,
            TargetEntityType = route.TargetEntityType
        };

    private static SyncScheduleRequest ToScheduleRequest(CreateScheduleRequest request) =>
        new(
            request.Name,
            request.PolicyId,
            request.PolicyVersion,
            request.CronExpression,
            request.TimeZone,
            request.Enabled);

    private static SyncScheduleRequest ToScheduleRequest(
        CreateScheduleVersionRequest request) => new(
            request.Name,
            request.PolicyId,
            request.PolicyVersion,
            request.CronExpression,
            request.TimeZone,
            request.Enabled);

    private static int Offset(
        ControlCursorProtector cursors,
        string? cursor,
        string resource,
        ControlRequestContext control,
        int pageSize)
    {
        ValidatePageSize(pageSize);
        return string.IsNullOrWhiteSpace(cursor)
            ? 0
            : cursors.UnprotectOffset(cursor, resource, control.TenantId);
    }

    private static IResult ListVendorsAsync()
    {
        return Results.Ok(new VendorCatalogResponse(new[]
        {
                BuildVendor("HaloPSA", "HaloPSA",
                    "PSA platform integration"),
                BuildVendor("NetSuite", "NetSuite",
                    "Oracle NetSuite ERP integration"),
                BuildVendor("NCentral", "N-central",
                    "N-able N-central RMM integration"),
                BuildVendor(EntitySyncVendors.AgentController, "AgentController",
                    "LISS Tech AgentController integration"),
                BuildVendor(EntitySyncVendors.BillCom, "Bill.com",
                    "Bill.com spend management integration"),
                BuildVendor(EntitySyncVendors.OrchestraMSP, "OrchestraMSP",
                    "OrchestraMSP platform integration"),
                BuildVendor(EntitySyncVendors.SophosCentral, "Sophos Central",
                    "Sophos Central cybersecurity platform integration")
            }));
    }

    private static VendorCatalogItem BuildVendor(string vendor, string displayName, string description)
    {
        var capabilities = EntityAdapterCapabilities.ForVendor(vendor);
        var entityTypes = capabilities.EntityTypes
            .Select(entityType => entityType.EntityType)
            .ToArray();
        return new VendorCatalogItem(vendor, $"{displayName} ({description})", entityTypes);
    }

    private static async Task<IResult> ListEntityRefreshesAsync(
        string connectionId,
        IEntityRefreshStateRepository states,
        ControlRequestContext control,
        string? entityType = null,
        CancellationToken cancellationToken = default)
    {
        var items = await states.ListByConnectionAsync(
            control.TenantId, connectionId, entityType, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(new EntityRefreshListResponse(
            items.Select(EntityRefreshResponse.From).ToArray()));
    }

    private static async Task<IResult> QueueEntityRefreshAsync(
        string connectionId,
        QueueEntityRefreshRequest request,
        EntityRefreshService service,
        ControlRequestContext control,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new BadHttpRequestException("A request body is required.");
        var state = await service.QueueAsync(
            control.TenantId, connectionId, request.EntityType,
            request.ExpectedGeneration, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(EntityRefreshResponse.From(state),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> AcceptAtomicEntityEventAsync(
        string connectionId,
        AtomicEntityEventRequest request,
        EntityRefreshService service,
        ControlRequestContext control,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new BadHttpRequestException("A request body is required.");
        ExternalEntity? entity = null;
        if (request.Operation == EntityAtomicOperation.Upsert && request.Entity is null)
            throw new BadHttpRequestException("Upsert events require an entity payload.");
        if (request.Entity is not null)
        {
            entity = new ExternalEntity
            {
                Id = request.Entity.Id,
                Name = request.Entity.Name ?? string.Empty,
                Email = request.Entity.Email,
                Phone = request.Entity.Phone,
                Website = request.Entity.Website,
                Domain = request.Entity.Domain,
                ParentId = request.Entity.ParentId,
                ParentEntityType = request.Entity.ParentEntityType,
                Version = request.Entity.Version,
                LifecycleStatus = request.Entity.LifecycleStatus,
                IsActive = request.Entity.IsActive
            };
            foreach (var pair in request.Entity.CustomFields)
                entity.CustomFields[pair.Key] = pair.Value;
        }
        var atomicEvent = new EntityAtomicEvent(
            request.EventId,
            connectionId,
            request.EntityType,
            request.Operation,
            entity,
            request.SourceCursor,
            request.SourceUpdatedAt);
        var outcome = await service.AcceptAtomicEventAsync(
            control.TenantId, connectionId, atomicEvent,
            request.ExpectedGeneration, cancellationToken).ConfigureAwait(false);
        var entityId = entity?.Id ?? string.Empty;
        var response = new AtomicEntityEventResponse(
            request.EventId,
            connectionId,
            request.EntityType,
            entityId,
            request.Operation.ToString(),
            outcome.Kind.ToString(),
            outcome.AppliedAt,
            outcome.Record?.PayloadHash);
        return Results.Json(response, statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Page<T>(
        IReadOnlyList<T> values,
        int pageSize,
        int offset,
        ControlCursorProtector cursors,
        string resource,
        ControlRequestContext control)
    {
        var items = values.Take(pageSize).ToArray();
        var next = values.Count > pageSize
            ? cursors.ProtectOffset(resource, control.TenantId, offset + items.Length)
            : null;
        return Results.Ok(new ControlPage<T>(items, next));
    }

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > 100) throw new ControlPageSizeException();
    }

    private static Guid RequireCorrelationId(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(
                CorrelationHeaderName, out var values)
            || values.Count != 1
            || !Guid.TryParseExact(values[0], "D", out var correlationId)
            || correlationId == Guid.Empty)
            throw new BadHttpRequestException(
                $"A single UUID {CorrelationHeaderName} header is required.");
        http.TraceIdentifier = correlationId.ToString("D");
        return correlationId;
    }

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static (int Status, string Code, string Detail) MapException(Exception? exception) =>
        exception switch
        {
            InvalidControlCursorException => (
                StatusCodes.Status400BadRequest,
                "INVALID_CURSOR",
                "The opaque cursor is invalid for this resource and tenant."),
            ControlPageSizeException => (
                StatusCodes.Status400BadRequest,
                "PAGE_SIZE_OUT_OF_RANGE",
                "Page size must be between 1 and 100."),
            BadHttpRequestException or ArgumentException or FormatException
                or Cronos.CronFormatException or TimeZoneNotFoundException
                or NotSupportedException => (
                StatusCodes.Status400BadRequest,
                "INVALID_REQUEST",
                "The request is invalid."),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "NOT_FOUND",
                "The requested tenant resource was not found."),
            EntitySyncIdempotencyRecoveryUnknownException => (
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_RECOVERY_UNKNOWN",
                "The external mutation outcome cannot be recovered exactly."),
            IdempotencyConflictException or CanonicalChangeConflictException => (
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_CONFLICT",
                "The request conflicts with an existing durable identity."),
            EntitySyncDependencyUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "DEPENDENCY_UNAVAILABLE",
                "A required control-plane dependency is unavailable."),
            InvalidOperationException => (
                StatusCodes.Status409Conflict,
                "STATE_CONFLICT",
                "The resource state changed or does not permit this operation."),
            OperationCanceledException => (
                StatusCodes.Status503ServiceUnavailable,
                "OPERATION_CANCELLED",
                "The operation was cancelled before completion."),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "CONTROL_API_UNAVAILABLE",
                "The control API could not complete the request.")
        };


    private sealed class ControlPageSizeException : ArgumentOutOfRangeException;
}
