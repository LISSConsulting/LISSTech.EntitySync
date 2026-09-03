using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Application;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class SyncTools
{
    [McpServerTool]
    [Description("Plan an EntitySync, Entity Sync, ES, client sync, customer sync, or account/company reconciliation between supported vendors. Planning performs no vendor writes and retains observations in EntitySync's durable graph. Use sourceSearch/sourceCount for focused work and sourceEntityId to assert the immutable source ID. Inspect every page and approve its digest before apply.")]
    public static async Task<string> CreateSyncPlan(
        EntitySyncService service,
        McpRequestContext context,
        [Description("Source vendor: HaloPSA, NetSuite, NCentral, Bill.com, or Sophos Central")] string sourceVendor,
        [Description("Target vendor: HaloPSA, NCentral, Bill.com, Sophos Central, or AgentController. NetSuite is read-only.")] string targetVendor,
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
    [Description("Review one page of an EntitySync/ES plan. Inspect every page and its exact source/target records before approval.")]
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
    [Description("Approve the exact digest of a fully inspected EntitySync/ES client, customer, account, or company sync plan. Approval is consumed by apply.")]
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
    [Description("Dry-run or execute an approved EntitySync/ES client, customer, account, or company sync plan. apply=false previews synchronously; apply=true starts writes in the background. Poll get_sync_plan_apply until Applied or Failed; repeated starts never retry writes.")]
    public static async Task<string> ApplySyncPlan(
        EntitySyncService service,
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan")] string planId,
        [Description("False performs a synchronous read-only dry run. True starts background writes and returns immediately.")] bool apply = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (apply)
            {
                var snapshot = coordinator.Start(context.TenantId, planId);
                return JsonSerializer.Serialize(new { success = true, snapshot }, JsonOptions);
            }

            var result = await service.ApplyAsync(context.TenantId, planId, false, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { success = result.Success, result }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EntityExclusionUnavailableException)
        {
            return Error("Permanent exclusions could not be obtained; create actions are blocked.");
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
            return Error("Plan apply failed unexpectedly. Check the server logs for the correlated operation.");
        }
    }

    [McpServerTool]
    [Description("Read-only: check progress and terminal status for an EntitySync/ES sync started by apply_sync_plan with apply=true.")]
    public static string GetSyncPlanApply(
        EntitySyncApplyCoordinator coordinator,
        McpRequestContext context,
        [Description("Plan ID returned from create_sync_plan and started with apply_sync_plan")] string planId)
    {
        try
        {
            var snapshot = coordinator.Get(context.TenantId, planId);
            return JsonSerializer.Serialize(new { success = true, snapshot }, JsonOptions);
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
            return Error("Plan apply status lookup failed.");
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
