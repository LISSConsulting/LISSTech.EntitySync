using System.Net.Http.Headers;
using System.Text;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Mcp.ControlApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[Collection(nameof(ControlApiCollection))]
public sealed class ControlSurfaceParityTests
{
    private static readonly Guid PolicyId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlanId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ApprovalId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("connections.list")]
    [InlineData("plans.create")]
    [InlineData("plans.inspect")]
    [InlineData("plans.approve")]
    [InlineData("runs.dry-run")]
    [InlineData("runs.apply")]
    [InlineData("exclusions.list")]
    public async Task Mcp_and_http_delegate_to_same_application_command(string operation)
    {
        var mcp = new RecordingControlCommands();
        var http = new RecordingControlCommands();

        await InvokeMcpAsync(operation, mcp);
        try
        {
            await InvokeHttpAsync(operation, http);
        }
        catch (RecordingCompleteException)
        {
        }

        Assert.Equal([operation], mcp.Operations);
        Assert.Equal(mcp.Operations, http.Operations);
        Assert.Equal(mcp.Calls, http.Calls);
    }

    [Theory]
    [InlineData("plans.create", "/api/v1/control/plans")]
    [InlineData("plans.approve", "/api/v1/control/plans/22222222-2222-2222-2222-222222222222/approvals")]
    [InlineData("runs.dry-run", "/api/v1/control/plans/22222222-2222-2222-2222-222222222222/dry-run")]
    [InlineData("runs.apply", "/api/v1/control/plans/22222222-2222-2222-2222-222222222222/apply")]
    public async Task Authenticated_http_filter_preserves_the_caller_key_used_by_mcp(
        string operation,
        string path)
    {
        var mcp = new RecordingControlCommands();
        await InvokeMcpAsync(operation, mcp);

        var http = new RecordingControlCommands();
        using var factory = new ControlApiFactory(http, executeControlCommands: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Test");
        client.DefaultRequestHeaders.Add(
            "X-Test-Claims",
            operation is "plans.approve" or "runs.apply"
                ? "tid=tenant-a;oid=tenant-a;scp=EntitySync.Approve"
                : "tid=tenant-a;oid=tenant-a;scp=EntitySync.Operate");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(
            IdempotencyEndpointFilter.HeaderName, CallerKey(operation));
        request.Headers.Add(
            "X-Correlation-ID",
            "11111111-1111-4111-8111-111111111111");
        var body = RequestBody(operation);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(mcp.Operations, http.Operations);
        Assert.Equal(mcp.Calls, http.Calls);
    }

    private static string CallerKey(string operation) =>
        operation switch
        {
            "plans.create" => "create-key",
            "plans.approve" => "approve-key",
            "runs.dry-run" => "dry-key",
            "runs.apply" => "apply-key",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static string? RequestBody(string operation) =>
        operation switch
        {
            "plans.create" =>
                $$"""{"policyId":"{{PolicyId:D}}","planLifetimeMinutes":60}""",
            "plans.approve" => $$"""{"digest":"{{new string('a', 64)}}"}""",
            "runs.apply" => $$"""{"approvalId":"{{ApprovalId:D}}"}""",
            "runs.dry-run" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static async Task InvokeMcpAsync(
        string operation,
        IEntitySyncControlCommands commands)
    {
        var context = new McpRequestContext("tenant-a", false);
        switch (operation)
        {
            case "connections.list":
                await ConnectionTools.ListConnections(
                    new ServiceCollection().BuildServiceProvider(), commands, context);
                break;
            case "plans.create":
                await SyncTools.CreateSyncPlan(
                    commands, context, PolicyId.ToString(), "create-key");
                break;
            case "plans.inspect":
                await SyncTools.GetSyncPlan(
                    commands, context, PlanId.ToString());
                break;
            case "plans.approve":
                await SyncTools.ApproveSyncPlan(
                    commands, context, PlanId.ToString(), new string('a', 64), "approve-key");
                break;
            case "runs.dry-run":
                await SyncTools.ApplySyncPlan(
                    commands, context, PlanId.ToString(), "dry-key");
                break;
            case "runs.apply":
                await SyncTools.ApplySyncPlan(
                    commands, context, PlanId.ToString(), "apply-key", true,
                    ApprovalId.ToString());
                break;
            case "exclusions.list":
                await ExclusionTools.ListEntityExclusions(
                    commands, context, NullLoggerFactory.Instance, "NetSuite", "HaloPSA");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async Task InvokeHttpAsync(
        string operation,
        IEntitySyncControlCommands commands)
    {
        var context = new ControlRequestContext(
            "tenant-a",
            "tenant-a",
            ControlActorKind.Delegated,
            new HashSet<string>(StringComparer.Ordinal));
        switch (operation)
        {
            case "connections.list":
                await ControlHttpOperations.ListConnectionsAsync(commands, context, default);
                break;
            case "plans.create":
                await ControlHttpOperations.CreatePlanAsync(
                    commands,
                    context,
                    new CreatePlanRequest(PolicyId, null, null, null, null, 60),
                    "create-key",
                    default);
                break;
            case "plans.inspect":
                await ControlHttpOperations.InspectPlanAsync(
                    commands, context, PlanId, 1, 25, default);
                break;
            case "plans.approve":
                await ControlHttpOperations.ApprovePlanAsync(
                    commands, context, PlanId, new string('a', 64),
                    "approve-key", default);
                break;
            case "runs.dry-run":
                await ControlHttpOperations.QueueDryRunAsync(
                    commands, context, PlanId, "dry-key", Guid.NewGuid(), default);
                break;
            case "runs.apply":
                await ControlHttpOperations.QueueApplyAsync(
                    commands, context, PlanId, ApprovalId,
                    "apply-key", Guid.NewGuid(), default);
                break;
            case "exclusions.list":
                await ControlHttpOperations.ListExclusionsAsync(
                    commands,
                    context,
                    new EntityExclusionRouteRequest
                    {
                        TenantId = context.TenantId,
                        SourceVendor = "NetSuite",
                        TargetVendor = "HaloPSA"
                    },
                    default);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class RecordingControlCommands : IEntitySyncControlCommands
    {
        private readonly List<string> operations = [];
        private readonly List<ControlCall> calls = [];


        public IReadOnlyList<string> Operations => operations;
        public IReadOnlyList<ControlCall> Calls => calls;


        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListConnectionsAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            operations.Add("connections.list");
            calls.Add(new ControlCall(
                "connections.list", tenantId, null, null, null));
            return Task.FromResult<IReadOnlyList<EntitySyncConnectionDefinition>>([]);
        }

        public Task<DurablePlanCommandResult> CreatePlanAsync(
            CreateDurablePlanRequest request,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            operations.Add("plans.create");
            calls.Add(new ControlCall(
                "plans.create",
                request.TenantId,
                request.PolicyId,
                request.IdempotencyKey,
                actor.ActorId));
            throw new RecordingCompleteException();
        }
        public Task<EntitySyncDurablePlan> ImportPlanAsync(
            string tenantId,
            EntitySyncDurablePlanManifest manifest,
            string idempotencyKey,
            EntitySyncActor actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();


        public Task<DurablePlanInspectionPage> InspectPlanAsync(
            string tenantId,
            Guid planId,
            int page,
            int pageSize,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            operations.Add("plans.inspect");
            calls.Add(new ControlCall(
                "plans.inspect", tenantId, planId, $"{page}:{pageSize}", actor.ActorId));
            throw new RecordingCompleteException();
        }

        public Task<DurablePlanApprovalResult> ApprovePlanAsync(
            string tenantId,
            Guid planId,
            string digest,
            string idempotencyKey,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            operations.Add("plans.approve");
            calls.Add(new ControlCall(
                "plans.approve", tenantId, planId, $"{digest}:{idempotencyKey}", actor.ActorId));
            throw new RecordingCompleteException();
        }

        public Task<EntitySyncOperation> QueueDryRunAsync(
            string tenantId,
            Guid planId,
            string idempotencyKey,
            Guid correlationId,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            operations.Add("runs.dry-run");
            calls.Add(new ControlCall(
                "runs.dry-run", tenantId, planId, idempotencyKey, actor.ActorId));
            throw new RecordingCompleteException();
        }

        public Task<EntitySyncOperation> QueueApplyAsync(
            string tenantId,
            Guid planId,
            Guid approvalId,
            string idempotencyKey,
            Guid correlationId,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            operations.Add("runs.apply");
            calls.Add(new ControlCall(
                "runs.apply",
                tenantId,
                planId,
                $"{approvalId}:{idempotencyKey}",
                actor.ActorId));
            throw new RecordingCompleteException();
        }

        public Task<IReadOnlyList<EntityExclusion>> ListExclusionsAsync(
            EntityExclusionRouteRequest request,
            CancellationToken cancellationToken)
        {
            operations.Add("exclusions.list");
            calls.Add(new ControlCall(
                "exclusions.list",
                request.TenantId,
                null,
                $"{request.SourceVendor}:{request.TargetVendor}",
                null));
            return Task.FromResult<IReadOnlyList<EntityExclusion>>([]);
        }
    }
    private sealed record ControlCall(
        string Operation,
        string TenantId,
        Guid? ResourceId,
        string? Token,
        string? ActorId);


    private sealed class RecordingCompleteException : Exception;
}
