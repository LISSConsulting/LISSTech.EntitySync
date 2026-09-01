using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Mcp.ControlApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

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
                    commands, context, PlanId, "dry-key", default);
                break;
            case "runs.apply":
                await ControlHttpOperations.QueueApplyAsync(
                    commands, context, PlanId, ApprovalId,
                    "apply-key", default);
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
