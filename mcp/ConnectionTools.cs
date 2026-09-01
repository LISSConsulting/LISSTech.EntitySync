using System.ComponentModel;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class ConnectionTools
{
    [McpServerTool]
    [Description("Connect a tenant-scoped vendor adapter using server-managed configuration. Remote callers cannot supply endpoints or credentials.")]
    public static async Task<string> ConnectVendor(
        IServiceProvider services,
        IServerManagedEntityAdapterFactory adapterFactory,
        ConnectionDefinitionService? definitions,
        McpRequestContext context,
        [Description("Vendor name: HaloPSA, NetSuite, NCentral, AgentController, Bill.com, or OrchestraMSP")] string vendor,
        [Description("Stable connection ID. Use distinct IDs for multiple accounts of the same vendor.")] string? connectionId = null,
        [Description("Local stdio only: named DPAPI profile. HTTP deployments use server environment configuration.")] string? profileName = null,
        [Description("Optional Orchestra platform-instance UUID for this source connection. Required only for nested Orchestra creates.")] Guid? platformInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string>? secretConfiguration = null;
        try
        {
            var normalized = EntitySyncVendors.Normalize(vendor);
            if (context.AllowProfiles)
            {
                return await ConnectLocalAsync(
                    services.GetRequiredService<IEntityConnectionRepository>(),
                    adapterFactory,
                    context,
                    normalized,
                    connectionId,
                    profileName,
                    platformInstanceId,
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(profileName))
                return Error("Profiles are disabled for remote MCP transport.");
            var configuration = adapterFactory.GetConnectionConfiguration(
                normalized,
                profileSettings: null);
            secretConfiguration = configuration.SecretConfiguration;
            definitions ??= services.GetRequiredService<ConnectionDefinitionService>();
            var resolvedConnectionId = string.IsNullOrWhiteSpace(connectionId)
                ? normalized.ToLowerInvariant()
                : connectionId.Trim();
            var request = new ConnectionDefinitionRequest(
                normalized,
                resolvedConnectionId,
                normalized,
                configuration.PublicConfiguration,
                configuration.SecretConfiguration,
                platformInstanceId ?? configuration.PlatformInstanceId);
            EntitySyncConnectionDefinition definition;
            try
            {
                var current = await definitions.GetAsync(
                    context.TenantId,
                    resolvedConnectionId,
                    cancellationToken).ConfigureAwait(false);
                definition = await definitions.UpdateAsync(
                    context.TenantId,
                    resolvedConnectionId,
                    current.Generation,
                    request,
                    new EntitySyncActor(context.Actor),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectionNotFoundException)
            {
                definition = await definitions.CreateAsync(
                    context.TenantId,
                    request,
                    new EntitySyncActor(context.Actor),
                    cancellationToken).ConfigureAwait(false);
            }
            return JsonSerializer.Serialize(new
            {
                success = true,
                Id = definition.ConnectionId,
                definition.Vendor,
                definition.Generation,
                definition.PlatformInstanceId
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            return Error(exception.Message);
        }
        catch
        {
            return Error(
                "Connection failed. Check server logs for the correlated operation.");
        }
        finally
        {
            if (secretConfiguration is IDictionary<string, string> secrets)
                secrets.Clear();
        }
    }

    private static async Task<string> ConnectLocalAsync(
        IEntityConnectionRepository connections,
        IServerManagedEntityAdapterFactory adapterFactory,
        McpRequestContext context,
        string vendor,
        string? connectionId,
        string? profileName,
        Guid? platformInstanceId,
        CancellationToken cancellationToken)
    {
        IEntityAdapter? adapter = null;
        IReadOnlyDictionary<string, string>? localSecretConfiguration = null;
        using var admission = connections.BeginRegistration(
            context.TenantId,
            connectionId,
            vendor);
        try
        {
            var profile = FindProfile(vendor, profileName);
            var configuration = adapterFactory.GetConnectionConfiguration(
                vendor, profile?.Settings);
            localSecretConfiguration = configuration.SecretConfiguration;
            adapter = await adapterFactory
                .CreateAsync(vendor, profile?.Settings, cancellationToken)
                .ConfigureAwait(false);
            var registration = connections.Register(
                context.TenantId,
                admission.ConnectionId,
                adapter,
                platformInstanceId ?? configuration.PlatformInstanceId);
            adapter = null;
            return JsonSerializer.Serialize(new
            {
                success = true,
                registration.Id,
                registration.Vendor,
                registration.Generation,
                registration.PlatformInstanceId,
                profile = profile?.Name
            });
        }
        finally
        {
            if (adapter is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (adapter is IDisposable disposable)
                disposable.Dispose();
            if (localSecretConfiguration is IDictionary<string, string> secrets)
                secrets.Clear();
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
        IConnectionRuntimeFactory connections,
        McpRequestContext context,
        [Description("Vendor name")] string vendor,
        [Description("Connection ID. Required when multiple connections exist for this vendor.")] string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var lease = await connections.AcquireCurrentAsync(
                context.TenantId,
                vendor,
                connectionId,
                cancellationToken).ConfigureAwait(false);
            var connected = await lease.Adapter.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                Id = lease.Definition.ConnectionId,
                lease.Definition.Vendor,
                connected
            });
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
    public static async Task<string> ListConnections(
        IServiceProvider services,
        IEntitySyncControlCommands commands,
        McpRequestContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AllowProfiles)
        {
            var local = services.GetRequiredService<IEntityConnectionRepository>()
                .List(context.TenantId)
                .Select(connection => new
                {
                    Id = connection.Id,
                    connection.Vendor,
                    connection.Generation,
                    connection.PlatformInstanceId,
                    Enabled = true
                });
            return JsonSerializer.Serialize(new { success = true, connections = local });
        }

        var result = (await commands.ListConnectionsAsync(
                context.TenantId,
                cancellationToken).ConfigureAwait(false))
            .Select(connection => new
            {
                Id = connection.ConnectionId,
                connection.Vendor,
                connection.Generation,
                connection.PlatformInstanceId,
                connection.Enabled
            });
        return JsonSerializer.Serialize(new { success = true, connections = result });
    }

    [McpServerTool]
    [Description("Read a bounded page of canonical entities from a tenant-scoped connection.")]
    public static async Task<string> GetEntities(
        IConnectionRuntimeFactory connections,
        McpRequestContext context,
        [Description("Vendor name")] string vendor,
        [Description("Entity type")] string entityType = "Customer",
        [Description("Connection ID. Required when multiple connections exist for this vendor.")] string? connectionId = null,
        [Description("Optional name search filter")] string? search = null,
        [Description("Include inactive entities")] bool includeInactive = false,
        [Description("Maximum entities, from 1 through 1000")] int count = 100,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) return Error("Count must be between 1 and 1000.");
        if (search?.Length > 512) return Error("Search cannot exceed 512 characters.");
        try
        {
            await using var lease = await connections.AcquireCurrentAsync(
                context.TenantId,
                vendor,
                connectionId,
                cancellationToken).ConfigureAwait(false);
            var entities = await lease.Adapter.GetEntitiesAsync(new EntityQuery
            {
                EntityType = entityType,
                Search = search,
                IncludeInactive = includeInactive,
                FullObjects = false,
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
                entity.IsActive,
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
