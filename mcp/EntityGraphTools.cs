using System.ComponentModel;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class EntityGraphTools
{
    private static readonly string[] SensitiveFieldTerms =
        ["password", "secret", "token", "authorization", "credential", "apikey", "privatekey"];

    [McpServerTool]
    [Description("Read vendor entity records retained by EntitySync. This queries EntitySync's durable record store rather than calling a vendor API.")]
    public static async Task<string> GetEntityRecords(
        IEntityGraphRepository graph,
        McpRequestContext context,
        [Description("Optional vendor name filter")] string? vendor = null,
        [Description("Optional connection ID filter")] string? connectionId = null,
        [Description("Optional entity type filter")] string? entityType = null,
        [Description("Optional case-insensitive name search or exact entity ID")] string? search = null,
        [Description("Maximum records, from 1 through 1000")] int count = 100,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) return Error("Count must be between 1 and 1000.");
        if (search?.Length > 512) return Error("Search cannot exceed 512 characters.");
        try
        {
            var records = await graph.QueryEntitiesAsync(
                new EntityGraphQuery(
                    context.TenantId,
                    vendor,
                    connectionId,
                    entityType,
                    search,
                    count),
                cancellationToken).ConfigureAwait(false);
            var result = records.Select(record => new
            {
                record.Key.Vendor,
                record.Key.ConnectionId,
                record.Key.EntityType,
                record.Key.EntityId,
                record.Entity.Name,
                record.Entity.Email,
                record.Entity.Phone,
                record.Entity.Website,
                record.Entity.Domain,
                record.Entity.PrimarySiteId,
                record.Entity.PrimarySiteName,
                record.Entity.PrimaryAddress,
                record.Entity.BillingAddress,
                record.Entity.ShippingAddress,
                record.Entity.IsActive,
                record.Entity.CreatedAt,
                record.Entity.UpdatedAt,
                externalIds = FilterFields(record.Entity.ExternalIds),
                customFields = FilterFields(record.Entity.CustomFields),
                record.PayloadHash,
                record.FirstObservedAt,
                record.LastObservedAt,
                record.LastPlanId
            });
            return JsonSerializer.Serialize(new { success = true, count = records.Count, records = result });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error("EntitySync record query failed.");
        }
    }

    [McpServerTool]
    [Description("Read EntitySync's durable relationship graph around one exact vendor entity. Relationships are returned regardless of edge direction.")]
    public static async Task<string> GetEntityRelationships(
        IEntityGraphRepository graph,
        McpRequestContext context,
        [Description("Vendor name")] string vendor,
        [Description("Connection ID")] string connectionId,
        [Description("Entity type")] string entityType,
        [Description("Vendor entity ID")] string entityId,
        [Description("Optional relationship type filter, such as EquivalentTo")] string? relationshipType = null,
        [Description("Maximum relationships, from 1 through 1000")] int count = 100,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) return Error("Count must be between 1 and 1000.");
        try
        {
            var relationships = await graph.QueryRelationshipsAsync(
                new EntityGraphRelationshipQuery(
                    new EntityGraphNodeKey(
                        context.TenantId,
                        vendor,
                        connectionId,
                        entityType,
                        entityId),
                    relationshipType,
                    count),
                cancellationToken).ConfigureAwait(false);
            var result = relationships.Select(relationship => new
            {
                relationship.Source,
                relationship.Target,
                relationship.RelationshipType,
                relationship.Status,
                relationship.MatchType,
                relationship.Score,
                relationship.Evidence,
                relationship.FirstObservedAt,
                relationship.LastObservedAt,
                relationship.ConfirmedAt,
                relationship.LastPlanId
            });
            return JsonSerializer.Serialize(new { success = true, count = relationships.Count, relationships = result });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error("EntitySync relationship query failed.");
        }
    }

    private static IReadOnlyDictionary<string, TValue> FilterFields<TValue>(
        IReadOnlyDictionary<string, TValue> fields) =>
        fields.Where(pair => !IsSensitiveName(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return SensitiveFieldTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message });
}
