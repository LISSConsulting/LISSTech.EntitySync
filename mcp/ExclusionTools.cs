using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Application;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class ExclusionTools
{
    [McpServerTool]
    [Description("List active permanent source-entity exclusions for one exact synchronization route. An empty result is a successfully loaded empty policy.")]
    public static async Task<string> ListEntityExclusions(
        EntityExclusionService service,
        McpRequestContext context,
        ILoggerFactory loggerFactory,
        [Description("Source vendor")] string sourceVendor,
        [Description("Target vendor")] string targetVendor,
        string? sourceConnectionId = null,
        string? sourceEntityType = null,
        string? targetConnectionId = null,
        string? targetEntityType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var exclusions = await service.ListAsync(Route(context, sourceVendor, targetVendor, sourceConnectionId, sourceEntityType, targetConnectionId, targetEntityType), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                exclusions = exclusions.Select(exclusion => new
                {
                    exclusion.Id,
                    exclusion.Route.SourceVendor,
                    exclusion.Route.SourceConnectionId,
                    exclusion.Route.SourceEntityType,
                    exclusion.Route.TargetVendor,
                    exclusion.Route.TargetConnectionId,
                    exclusion.Route.TargetEntityType,
                    exclusion.SourceEntityId,
                    exclusion.SourceName,
                    exclusion.Reason,
                    exclusion.CreatedBy,
                    exclusion.CreatedAt
                })
            }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("LISSTech.EntitySync.Mcp.ExclusionTools")
                .LogError(ex, "Failed to list permanent EntitySync exclusions.");
            return Error("Exclusions could not be obtained. Create-missing planning and apply remain fail-closed.");
        }
    }

    [McpServerTool]
    [Description("Permanently exclude one immutable source entity ID from create-missing operations on one exact synchronization route. AgentController authoritative routes do not permit exclusions.")]
    public static async Task<string> AddEntityExclusion(
        EntityExclusionService service,
        McpRequestContext context,
        string sourceVendor,
        string targetVendor,
        [Description("Immutable vendor source entity ID; never use a customer name as the ID")] string sourceEntityId,
        [Description("Current source display name, stored only for operator context")] string sourceName,
        [Description("Required operator reason for the permanent exclusion")] string reason,
        string? sourceConnectionId = null,
        string? sourceEntityType = null,
        string? targetConnectionId = null,
        string? targetEntityType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var exclusion = await service.AddAsync(
                Route(context, sourceVendor, targetVendor, sourceConnectionId, sourceEntityType, targetConnectionId, targetEntityType),
                sourceEntityId,
                sourceName,
                reason,
                context.Actor,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { success = true, exclusion }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("The permanent exclusion could not be stored.");
        }
    }

    [McpServerTool]
    [Description("Revoke one active permanent source-entity exclusion on one exact synchronization route.")]
    public static async Task<string> RemoveEntityExclusion(
        EntityExclusionService service,
        McpRequestContext context,
        string sourceVendor,
        string targetVendor,
        string sourceEntityId,
        string? sourceConnectionId = null,
        string? sourceEntityType = null,
        string? targetConnectionId = null,
        string? targetEntityType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var removed = await service.RevokeAsync(
                Route(context, sourceVendor, targetVendor, sourceConnectionId, sourceEntityType, targetConnectionId, targetEntityType),
                sourceEntityId,
                context.Actor,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { success = true, removed }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("The permanent exclusion could not be revoked.");
        }
    }

    private static EntityExclusionRouteRequest Route(
        McpRequestContext context,
        string sourceVendor,
        string targetVendor,
        string? sourceConnectionId,
        string? sourceEntityType,
        string? targetConnectionId,
        string? targetEntityType) => new()
        {
            TenantId = context.TenantId,
            SourceVendor = sourceVendor,
            SourceConnectionId = sourceConnectionId,
            SourceEntityType = sourceEntityType,
            TargetVendor = targetVendor,
            TargetConnectionId = targetConnectionId,
            TargetEntityType = targetEntityType
        };

    private static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
