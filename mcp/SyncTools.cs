using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class SyncTools
{
    [McpServerTool]
    [Description("Create an immutable tenant-scoped synchronization plan from an explicit persisted policy. Planning is read-only. Inspect every page and approve its digest before apply.")]
    public static async Task<string> CreateSyncPlan(
        DurablePlanService service,
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
            var plan = await service.CreatePlanAsync(
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
            var page = await service.GetPageAsync(
                context.TenantId, plan.PlanId, 1, 25,
                new EntitySyncActor(context.Actor), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                planId = plan.PlanId,
                status = "Draft",
                digest = plan.Digest,
                plan,
                page,
                nextPage = plan.ItemCount > page.Page * page.PageSize
                    ? page.Page + 1
                    : (int?)null
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

    public static async Task<string> CreateSyncPlan(
        EntitySyncService service,
        McpRequestContext context,
        string sourceVendor,
        string targetVendor,
        string? sourceConnectionId = null,
        string? targetConnectionId = null,
        string? sourceEntityType = null,
        string? targetEntityType = null,
        bool createMissing = false,
        bool includeInactive = false,
        int autoLinkScore = 90,
        int reviewScore = 70,
        string? sourceExternalIdName = null,
        string? targetCustomFieldName = null,
        string? sourceSearch = null,
        int? sourceCount = null,
        string? sourceEntityId = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = context.TenantId,
            SourceVendor = sourceVendor,
            SourceConnectionId = sourceConnectionId,
            TargetVendor = targetVendor,
            TargetConnectionId = targetConnectionId,
            SourceEntityType = sourceEntityType,
            SourceSearch = sourceSearch,
            SourceCount = sourceCount,
            SourceEntityId = sourceEntityId,
            TargetEntityType = targetEntityType,
            CreateMissing = createMissing,
            IncludeInactive = includeInactive,
            AutoLinkScore = autoLinkScore,
            ReviewScore = reviewScore,
            SourceExternalIdName = sourceExternalIdName,
            TargetCustomFieldName = targetCustomFieldName
        }, cancellationToken).ConfigureAwait(false);
        var page = service.GetPlan(context.TenantId, plan.Id);
        return JsonSerializer.Serialize(new
        {
            success = true,
            planId = plan.Id,
            plan.Status,
            page.Digest,
            plan.SourceVendor,
            plan.SourceEntityType,
            plan.TargetVendor,
            plan.TargetEntityType,
            sourceSelection = new
            {
                search = sourceSearch,
                count = sourceCount,
                entityId = sourceEntityId
            },
            actions = plan.Items.GroupBy(item => item.Action)
                .ToDictionary(group => group.Key, group => group.Count()),
            page.TotalItems,
            page.Page,
            page.PageSize,
            page.Items,
            nextPage = page.TotalItems > page.Page * page.PageSize
                ? page.Page + 1
                : (int?)null
        }, JsonOptions);
    }

    [McpServerTool]
    [Description("Inspect a page of a durable plan. Every page must be reviewed before approving the returned digest.")]
    public static async Task<string> GetSyncPlan(
        DurablePlanService service,
        McpRequestContext context,
        [Description("Durable plan ID returned from create_sync_plan")] string planId,
        [Description("One-based page number")] int page = 1,
        [Description("Items per page, from 1 through 100")] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await service.GetPageAsync(
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
        DurablePlanService service,
        McpRequestContext context,
        [Description("Durable plan ID returned from create_sync_plan")] string planId,
        [Description("Digest returned by get_sync_plan after reviewing every page")] string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var approval = await service.ApproveAsync(
                context.TenantId, Guid.Parse(planId), expectedDigest,
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
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan")] string planId,
        [Description("Stable caller-generated idempotency key for this exact request")] string idempotencyKey,
        [Description("False queues a read-only dry-run. True queues the approved vendor writes.")] bool apply = false,
        [Description("Exact approval ID; required once when apply=true")] string? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await coordinator.QueueAsync(
                context.TenantId,
                Guid.Parse(planId),
                string.IsNullOrWhiteSpace(approvalId) ? null : Guid.Parse(approvalId),
                idempotencyKey,
                new EntitySyncActor(context.Actor),
                apply,
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
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        [Description("Durable operation ID returned from apply_sync_plan")] string operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await coordinator.GetOperationAsync(
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

    public static async Task<string> ApplySyncPlan(
        EntitySyncService service,
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        string planId,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        if (apply)
            return JsonSerializer.Serialize(
                new { success = true, snapshot = coordinator.Start(context.TenantId, planId) },
                JsonOptions);
        var result = await service.ApplyAsync(
            context.TenantId, planId, false, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { success = result.Success, result }, JsonOptions);
    }

    public static string GetSyncPlanApply(
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        string planId) =>
        JsonSerializer.Serialize(
            new { success = true, snapshot = coordinator.Get(context.TenantId, planId) },
            JsonOptions);

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
