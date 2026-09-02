using System.ComponentModel;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class ConnectionTools
{
    [McpServerTool]
    [Description("Connect a tenant-scoped vendor adapter using server-managed configuration. Remote callers cannot supply endpoints or credentials.")]
    public static async Task<string> ConnectVendor(
        IEntityConnectionRepository connections,
        IServerManagedEntityAdapterFactory adapterFactory,
        McpRequestContext context,
        [Description("Vendor name: HaloPSA, NetSuite, NCentral, AgentController, Bill.com, or Sophos Central")] string vendor,
        [Description("Stable connection ID. Use distinct IDs for multiple accounts of the same vendor.")] string? connectionId = null,
        [Description("Local stdio only: named DPAPI profile. HTTP deployments use server environment configuration.")] string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        IEntityAdapter? adapter = null;
        IEntityConnectionAdmission? admission = null;
        try
        {
            var tenantId = context.TenantId;
            var normalized = EntitySyncVendors.Normalize(vendor);
            admission = connections.BeginRegistration(tenantId, connectionId, normalized);
            connectionId = admission.ConnectionId;
            if (!context.AllowProfiles && !string.IsNullOrWhiteSpace(profileName))
                throw new InvalidOperationException("Profiles are disabled for remote MCP transport.");
            var profile = context.AllowProfiles && !string.IsNullOrWhiteSpace(profileName)
                ? FindProfile(normalized, profileName)
                : null;
            adapter = await adapterFactory
                .CreateAsync(normalized, profile?.Settings, cancellationToken)
                .ConfigureAwait(false);

            var registration = connections.Register(tenantId, connectionId, adapter);
            adapter = null;
            return JsonSerializer.Serialize(new
            {
                success = true,
                registration.Id,
                registration.Vendor,
                registration.Generation,
                profile = profile?.Name
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
        catch
        {
            return Error("Connection failed. Check server logs for the correlated operation.");
        }
        finally
        {
            if (adapter is IDisposable disposable) disposable.Dispose();
            admission?.Dispose();
        }
    }

    [McpServerTool]
    [Description("List local EntitySync profiles. Profiles are disabled over remote HTTP transport.")]
    public static string ListProfiles(McpRequestContext context)
    {
        if (!context.AllowProfiles) return Error("Profiles are disabled for remote MCP transport.");
        try
        {
            var profiles = EntitySyncProfileStore.ListProfiles().Select(profile => new { profile.Name, profile.IsDefault, profile.Vendors });
            return JsonSerializer.Serialize(new { success = true, profiles });
        }
        catch
        {
            return Error("Profile listing failed.");
        }
    }

    [McpServerTool]
    [Description("Test a tenant-scoped vendor connection.")]
    public static async Task<string> TestConnection(
        IEntityConnectionRepository connections,
        McpRequestContext context,
        [Description("Vendor name")] string vendor,
        [Description("Connection ID. Required when multiple connections exist for this vendor.")] string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = connections.Acquire(context.TenantId, vendor, connectionId);
            var connection = lease.Connection;
            var connected = await connection.Adapter.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { success = true, connection.Id, connection.Vendor, connected });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error("Connection test failed.");
        }
    }

    [McpServerTool]
    [Description("List tenant-scoped connected vendor adapters.")]
    public static string ListConnections(IEntityConnectionRepository connections, McpRequestContext context)
    {
        var result = connections.List(context.TenantId).Select(connection => new { connection.Id, connection.Vendor, connection.Generation });
        return JsonSerializer.Serialize(new { success = true, connections = result });
    }

    [McpServerTool]
    [Description("Read a bounded page of canonical entities for factual questions and inspection. Search by name to narrow results. Responses include contact, site, address, lifecycle, external-ID, and custom-field data when the vendor provides them; set includeDetails=true for vendor detail reads.")]
    public static async Task<string> GetEntities(
        IEntityConnectionRepository connections,
        McpRequestContext context,
        [Description("Vendor name")] string vendor,
        [Description("Entity type")] string entityType = "Customer",
        [Description("Connection ID. Required when multiple connections exist for this vendor.")] string? connectionId = null,
        [Description("Optional name search filter")] string? search = null,
        [Description("Include inactive entities")] bool includeInactive = false,
        [Description("Maximum entities, from 1 through 1000. Keep this small when includeDetails is true.")] int count = 100,
        [Description("Request full vendor records. Use true for questions about addresses or other detailed fields.")] bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) return Error("Count must be between 1 and 1000.");
        if (search?.Length > 512) return Error("Search cannot exceed 512 characters.");
        try
        {
            using var lease = connections.Acquire(context.TenantId, vendor, connectionId);
            var connection = lease.Connection;
            var entities = await connection.Adapter.GetEntitiesAsync(new EntityQuery
            {
                EntityType = entityType,
                Search = search,
                IncludeInactive = includeInactive,
                FullObjects = includeDetails,
                Count = count
            }, cancellationToken).ConfigureAwait(false);
            var result = entities.Take(count).Select(entity => new
            {
                entity.Vendor,
                entity.EntityType,
                entity.Id,
                entity.Name,
                entity.Email,
                entity.Phone,
                entity.Website,
                entity.Domain,
                entity.PrimarySiteId,
                entity.PrimarySiteName,
                entity.PrimaryAddress,
                entity.BillingAddress,
                entity.ShippingAddress,
                entity.IsActive,
                entity.CreatedAt,
                entity.UpdatedAt,
                externalIds = FilterFields(entity.ExternalIds),
                customFields = FilterFields(entity.CustomFields)
            });
            return JsonSerializer.Serialize(new { success = true, count = Math.Min(entities.Count, count), entities = result });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error("Entity read failed.");
        }
    }

    private static ResolvedVendorProfile? FindProfile(string vendor, string? profileName)
    {
        var profiles = EntitySyncProfileStore.ListProfiles();
        var selected = string.IsNullOrWhiteSpace(profileName)
            ? profiles.FirstOrDefault(profile => profile.IsDefault)
            : profiles.FirstOrDefault(profile => profile.Name.Equals(profileName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            if (!string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException($"EntitySync profile '{profileName.Trim()}' was not found.");
            return null;
        }
        var vendorProfile = EntitySyncProfileStore.LoadProfile(selected.Name)
            .FirstOrDefault(profile => EntitySyncVendors.Normalize(profile.Vendor).Equals(vendor, StringComparison.OrdinalIgnoreCase));
        if (vendorProfile == null)
        {
            if (!string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException($"EntitySync profile '{selected.Name}' does not contain vendor '{vendor}'.");
            return null;
        }
        return new ResolvedVendorProfile(selected.Name, vendorProfile.Settings);
    }

    private static IReadOnlyDictionary<string, TValue> FilterFields<TValue>(IReadOnlyDictionary<string, TValue> fields)
    {
        return fields.Where(pair => !IsSensitiveName(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return new[] { "password", "secret", "token", "authorization", "credential", "apikey", "privatekey" }
            .Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message });
    private sealed record ResolvedVendorProfile(string Name, IReadOnlyDictionary<string, string> Settings);
}
