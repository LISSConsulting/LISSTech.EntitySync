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
    [Description("Create a tenant-scoped entity synchronization plan. Planning is read-only. Use sourceSearch/sourceCount to bound focused plans and sourceEntityId to assert the exact immutable source ID. Inspect every page and approve its digest before apply. Workflows requiring source integration-link writebacks must use PowerShell.")]
    public static async Task<string> CreateSyncPlan(
        EntitySyncService service,
        McpRequestContext context,
        [Description("Source vendor: HaloPSA, NetSuite, NCentral, or Bill.com")] string sourceVendor,
        [Description("Target vendor: HaloPSA, NetSuite, NCentral, or Bill.com")] string targetVendor,
        [Description("Source connection ID. Required when multiple connections exist for this vendor.")] string? sourceConnectionId = null,
        [Description("Target connection ID. Required when multiple connections exist for this vendor.")] string? targetConnectionId = null,
        [Description("Source entity type. Defaults to the vendor primary type.")] string? sourceEntityType = null,
        [Description("Target entity type. Defaults to the vendor primary type.")] string? targetEntityType = null,
        [Description("Create missing target entities during apply")] bool createMissing = false,
        [Description("Include inactive source entities")] bool includeInactive = false,
        [Description("Auto-link score threshold from 0 through 100")] int autoLinkScore = 90,
        [Description("Review score threshold from 0 through 100")] int reviewScore = 70,
        [Description("Source external ID used for matching and apply")] string? sourceExternalIdName = null,
        [Description("Target custom field used for matching and apply")] string? targetCustomFieldName = null,
        [Description("Optional vendor-side source name search used to bound focused plans")] string? sourceSearch = null,
        [Description("Optional maximum source entities from 1 through 5000")] int? sourceCount = null,
        [Description("Optional immutable source entity ID; the bounded source query must return exactly this entity")] string? sourceEntityId = null,
        CancellationToken cancellationToken = default)
    {
        try
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
                sourceSelection = new { search = sourceSearch, count = sourceCount, entityId = sourceEntityId },
                actions = plan.Items.GroupBy(item => item.Action).ToDictionary(group => group.Key, group => group.Count()),
                page.TotalItems,
                page.Page,
                page.PageSize,
                page.Items,
                nextPage = page.TotalItems > page.Page * page.PageSize ? page.Page + 1 : (int?)null
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
    [Description("Inspect a page of a plan. Every page must be reviewed before approving the returned digest.")]
    public static string GetSyncPlan(
        EntitySyncService service,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan")] string planId,
        [Description("One-based page number")] int page = 1,
        [Description("Items per page, from 1 through 100")] int pageSize = 25)
    {
        try
        {
            var result = service.GetPlan(context.TenantId, planId, page, pageSize);
            return JsonSerializer.Serialize(new { success = true, result, nextPage = result.TotalItems > result.Page * result.PageSize ? result.Page + 1 : (int?)null }, JsonOptions);
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
    [Description("Approve the exact inspected plan digest. Approval is required once and is consumed by apply.")]
    public static string ApproveSyncPlan(
        EntitySyncService service,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan")] string planId,
        [Description("Digest returned by get_sync_plan after reviewing every page")] string expectedDigest)
    {
        try
        {
            var digest = service.ApprovePlan(context.TenantId, planId, expectedDigest);
            return JsonSerializer.Serialize(new { success = true, planId, status = "Approved", digest });
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
