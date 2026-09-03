using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class SyncTools
{
    [McpServerTool]
    [Description("Create an immutable tenant-scoped EntitySync/ES plan for client sync, customer sync, account sync, company sync, or cross-vendor reconciliation from an explicit persisted policy. Planning is read-only. Inspect every page and approve its digest before apply.")]
    public static async Task<string> CreateSyncPlan(
        IEntitySyncControlCommands commands,
        McpRequestContext context,
        [Description("Explicit persisted sync policy ID")] string policyId,
        [Description("Stable caller-generated idempotency key for this exact planning request")] string idempotencyKey,
        [Description("Optional exact policy version; omitted means the latest enabled version")] int? policyVersion = null,
        [Description("Optional vendor-side source name search used to bound focused plans")] string? sourceSearch = null,
        [Description("Optional maximum source entities from 1 through 5000")] int? sourceCount = null,
        [Description("Optional immutable source entity ID; the bounded source query must return exactly this entity")] string? sourceEntityId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await commands.CreatePlanAsync(
                new CreateDurablePlanRequest
                {
                    TenantId = context.TenantId,
                    IdempotencyKey = idempotencyKey,
                    PolicyId = Guid.Parse(policyId),
                    PolicyVersion = policyVersion,
                    SourceSearch = sourceSearch,
                    SourceCount = sourceCount,
                    SourceEntityId = sourceEntityId
                },
                new EntitySyncActor(context.Actor),
                cancellationToken).ConfigureAwait(false);
            var plan = command.Result;
            return JsonSerializer.Serialize(new
            {
                success = true,
                planId = plan.PlanId,
                status = command.Plan.Status.ToString(),
                digest = plan.Digest,
                plan = command.Plan
            }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EntityExclusionUnavailableException)
        {
            return Error("Permanent exclusions could not be obtained; create-missing planning is blocked.");
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("Plan creation failed. Check the server logs for the correlated operation.");
        }
    }


    [McpServerTool]
    [Description("Inspect a page of a durable plan. Every page must be reviewed before approving the returned digest.")]
    public static async Task<string> GetSyncPlan(
        IEntitySyncControlCommands commands,
        McpRequestContext context,
        [Description("Durable plan ID returned from create_sync_plan")] string planId,
        [Description("One-based page number")] int page = 1,
        [Description("Items per page, from 1 through 100")] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await commands.InspectPlanAsync(
                context.TenantId, Guid.Parse(planId), page, pageSize,
                new EntitySyncActor(context.Actor), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                result,
                nextPage = result.Plan.ItemCount > result.Page * result.PageSize
                    ? result.Page + 1
                    : (int?)null
            }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("Plan inspection failed.");
        }
    }

    [McpServerTool]
    [Description("Approve the exact fully inspected durable-plan digest. The returned approval ID is consumed once by apply.")]
    public static async Task<string> ApproveSyncPlan(
        IEntitySyncControlCommands commands,
        McpRequestContext context,
        [Description("Durable plan ID returned from create_sync_plan")] string planId,
        [Description("Digest returned by get_sync_plan after reviewing every page")] string expectedDigest,
        [Description("Stable caller-generated idempotency key for this exact approval request")] string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var approval = await commands.ApprovePlanAsync(
                context.TenantId, Guid.Parse(planId), expectedDigest, idempotencyKey,
                new EntitySyncActor(context.Actor), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                planId = approval.PlanId,
                status = "Approved",
                digest = approval.Digest,
                approvalId = approval.ApprovalId,
                approval
            }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error("Plan approval failed. Reinspect the plan and confirm its current status and digest.");
        }
    }



    [McpServerTool]
    [Description("Queue a durable read-only dry-run or an approved apply. Returns the durable operation ID; poll get_sync_plan_apply by operation ID.")]
    public static async Task<string> ApplySyncPlan(
        IEntitySyncControlCommands commands,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan")] string planId,
        [Description("Stable caller-generated idempotency key for this exact request")] string idempotencyKey,
        [Description("False queues a read-only dry-run. True queues the approved vendor writes.")] bool apply = false,
        [Description("Exact approval ID; required once when apply=true")] string? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedPlanId = Guid.Parse(planId);
            var actor = new EntitySyncActor(context.Actor);
            var correlationId = Guid.NewGuid();
            var operation = apply
                ? await commands.QueueApplyAsync(
                    context.TenantId,
                    parsedPlanId,
                    Guid.Parse(approvalId ?? throw new ArgumentException(
                        "Approval ID is required for apply.", nameof(approvalId))),
                    idempotencyKey,
                    correlationId,
                    actor,
                    cancellationToken).ConfigureAwait(false)
                : await commands.QueueDryRunAsync(
                    context.TenantId,
                    parsedPlanId,
                    idempotencyKey,
                    correlationId,
                    actor,
                    cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                operationId = operation.OperationId,
                status = operation.Status.ToString()
            }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (IsSafeApplyStateError(ex.Message))
        {
            return Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("Plan apply queueing failed unexpectedly. Check the server logs for the correlated operation.");
        }
    }

    [McpServerTool]
    [Description("Read-only: get aggregate progress and terminal status for a durable operation returned by apply_sync_plan.")]
    public static async Task<string> GetSyncPlanApply(
        ISyncOperationRepository operations,
        McpRequestContext context,
        [Description("Durable operation ID returned from apply_sync_plan")] string operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await operations.GetAsync(
                context.TenantId, Guid.Parse(operationId), cancellationToken)
                .ConfigureAwait(false);
            if (operation is null)
                return Error("Durable sync operation was not found.");
            return JsonSerializer.Serialize(new { success = true, operation }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("Durable sync operation status lookup failed.");
        }
    }


    private static bool IsSafeApplyStateError(string message)
    {
        return message is
            "A plan connection changed after planning; create a new plan."
            or "Permanent exclusions changed after planning; create and inspect a new plan."
            or "Plan must be approved before apply."
            or "Approved plan digest no longer matches the plan."
            or "Plan is already being applied or has been consumed."
            or "Apply operation has not been started.";
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
