using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Adapters.BillCom;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Core;
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
        McpRequestContext context,
        [Description("Vendor name: HaloPSA, NetSuite, NCentral, AgentController, or Bill.com")] string vendor,
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
            if (!context.AllowProfiles && !string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("Profiles are disabled for remote MCP transport.");
            var profile = context.AllowProfiles ? FindProfile(normalized, profileName) : null;
            if (normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = RequireHttps(Resolve(profile, "HaloBaseUrl", "HALO_BASE_URL"), normalized);
                var token = await GetHaloAccessTokenAsync(
                    baseUrl,
                    Resolve(profile, "HaloClientId", "HALO_CLIENT_ID"),
                    Resolve(profile, "HaloClientSecret", "HALO_CLIENT_SECRET"),
                    ResolveOptional(profile, "HaloScope") ?? "all",
                    cancellationToken).ConfigureAwait(false);
                adapter = new HaloEntityAdapter(new HaloOptions
                {
                    BaseUrl = baseUrl,
                    AccessToken = token,
                    TopLevelId = ResolveInt(profile, "HaloTopLevelId", 1),
                    DefaultColour = ResolveOptional(profile, "HaloDefaultColour") ?? "#E83C4A",
                    NetSuiteCustomerIdField = ResolveOptional(profile, "HaloNetSuiteCustomerIdField") ?? "CFNetSuiteCustomerID",
                    NetSuiteCustomerIdFieldId = ResolveOptional(profile, "HaloNetSuiteCustomerIdFieldId", "HALO_NETSUITE_CUSTOMER_ID_FIELD_ID") ?? string.Empty,
                    NetSuiteCustomerNameField = ResolveOptional(profile, "HaloNetSuiteCustomerNameField", "HALO_NETSUITE_CUSTOMER_NAME_FIELD") ?? "CFNetSuiteCustomerName",
                    CustomerRelationshipId = ResolveInt(profile, "HaloCustomerRelationshipId", 0),
                    CustomerRelationshipName = ResolveOptional(profile, "HaloCustomerRelationshipName") ?? string.Empty,
                    CustomerTypeId = ResolveInt(profile, "HaloCustomerTypeId", 0),
                    CustomerTypeName = ResolveOptional(profile, "HaloCustomerTypeName") ?? string.Empty,
                    AccountManagerEmail = ResolveOptional(profile, "HaloAccountManagerEmail", "HALO_ACCOUNT_MANAGER_EMAIL"),
                    AccountManagerField = ResolveOptional(profile, "HaloAccountManagerField") ?? "CFassignedtam",
                    NCentralIntegrationId = ResolveInt(profile, "HaloNCentralIntegrationId", 0, "HALO_NCENTRAL_INTEGRATION_ID")
                });
            }
            else if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            {
                adapter = new NetSuiteEntityAdapter(new NetSuiteOptions
                {
                    AccountId = Resolve(profile, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID"),
                    ConsumerKey = Resolve(profile, "NetSuiteConsumerKey", "NETSUITE_CONSUMER_KEY"),
                    ConsumerSecret = Resolve(profile, "NetSuiteConsumerSecret", "NETSUITE_CONSUMER_SECRET"),
                    TokenId = Resolve(profile, "NetSuiteTokenId", "NETSUITE_TOKEN_ID"),
                    TokenSecret = Resolve(profile, "NetSuiteTokenSecret", "NETSUITE_TOKEN_SECRET")
                });
            }
            else if (normalized.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
            {
                adapter = new NCentralEntityAdapter(new NCentralOptions
                {
                    BaseUrl = RequireHttps(Resolve(profile, "NCentralBaseUrl", "NCENTRAL_BASE_URL"), normalized),
                    UserApiToken = Resolve(profile, "NCentralUserApiToken", "NCENTRAL_USER_API_TOKEN"),
                    ServiceOrgId = Resolve(profile, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID"),
                    SoapUsername = ResolveOptional(profile, "NCentralSoapUsername", "NCENTRAL_SOAP_USERNAME") ?? string.Empty,
                    SoapPassword = ResolveOptional(profile, "NCentralSoapPassword", "NCENTRAL_SOAP_PASSWORD") ?? string.Empty,
                    SoapEndpointPath = ResolveOptional(profile, "NCentralSoapEndpointPath", "NCENTRAL_SOAP_ENDPOINT_PATH") ?? "dms2/services2/ServerEI2",
                    SoapNamespace = ResolveOptional(profile, "NCentralSoapNamespace", "NCENTRAL_SOAP_NAMESPACE") ?? "http://ei2.nobj.nable.com/",
                    HaloPsaIdPropertyLabel = ResolveOptional(profile, "NCentralHaloPsaIdPropertyLabel", "NCENTRAL_HALOPSA_ID_PROPERTY_LABEL") ?? "HaloPSA Client ID",
                    NetSuiteIdPropertyLabel = ResolveOptional(profile, "NCentralNetSuiteIdPropertyLabel", "NCENTRAL_NETSUITE_ID_PROPERTY_LABEL") ?? "NetSuite Customer ID",
                    NetSuiteNamePropertyLabel = ResolveOptional(profile, "NCentralNetSuiteNamePropertyLabel", "NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL") ?? "NetSuite Customer Name"
                });
            }
            else if (EntitySyncVendors.IsAgentController(normalized))
            {
                if (profile != null)
                {
                    return Error("AgentController profiles are managed through the local EntitySync profile store. The MCP server does not accept profile credentials.");
                }

                adapter = await ConnectAgentControllerAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (EntitySyncVendors.IsBillCom(normalized))
            {
                adapter = new BillComEntityAdapter(new BillComOptions
                {
                    BaseUrl = RequireHttps(ResolveOptional(profile, "BillComBaseUrl", "BILLCOM_BASE_URL") ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields", normalized),
                    ApiToken = Resolve(profile, "BillComApiToken", "BILLCOM_API_TOKEN", "BILLSPEND_API_TOKEN"),
                    ClientFieldName = ResolveOptional(profile, "BillComClientFieldName", "BILLCOM_CLIENT_FIELD_NAME") ?? "Client"
                });
            }
            else
            {
                return Error("Unsupported vendor.");
            }

            var registration = connections.Register(tenantId, connectionId, adapter);
            adapter = null;
            return JsonSerializer.Serialize(new { success = true, registration.Id, registration.Vendor, registration.Generation, profile = profile?.Name });
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
    [Description("Read a bounded page of canonical entities from a tenant-scoped connection.")]
    public static async Task<string> GetEntities(
        IEntityConnectionRepository connections,
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
            using var lease = connections.Acquire(context.TenantId, vendor, connectionId);
            var connection = lease.Connection;
            var entities = await connection.Adapter.GetEntitiesAsync(new EntityQuery
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

    private static string Resolve(ResolvedVendorProfile? profile, string profileKey, params string[] envVars)
    {
        var value = ResolveOptional(profile, profileKey, envVars);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException($"Missing required server configuration: {string.Join(" or ", envVars)}.");
    }

    private static string? ResolveOptional(ResolvedVendorProfile? profile, string profileKey, params string[] envVars)
    {
        if (profile?.Settings.TryGetValue(profileKey, out var profileValue) == true && !string.IsNullOrWhiteSpace(profileValue)) return profileValue;
        foreach (var env in envVars)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static int ResolveInt(ResolvedVendorProfile? profile, string profileKey, int defaultValue, params string[] envVars)
    {
        var value = ResolveOptional(profile, profileKey, envVars);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) return result;
        throw new InvalidOperationException($"Server configuration '{profileKey}' must be an integer.");
    }

    private static string RequireHttps(string value, string vendor)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{vendor} server configuration must use an absolute HTTPS URL.");
        return uri.AbsoluteUri;
    }

    private static Task<IEntityAdapter> ConnectAgentControllerAsync(CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AGENTCONTROLLER_AUTH_BASE_URL"] = Environment.GetEnvironmentVariable("AGENTCONTROLLER_AUTH_BASE_URL"),
            ["AGENTCONTROLLER_ENTRA_TENANT_ID"] = Environment.GetEnvironmentVariable("AGENTCONTROLLER_ENTRA_TENANT_ID"),
            ["AGENTCONTROLLER_ENTRA_CLIENT_ID"] = Environment.GetEnvironmentVariable("AGENTCONTROLLER_ENTRA_CLIENT_ID"),
            ["AGENTCONTROLLER_ENTRA_CLIENT_SECRET"] = Environment.GetEnvironmentVariable("AGENTCONTROLLER_ENTRA_CLIENT_SECRET"),
            ["AGENTCONTROLLER_ENTRA_SCOPE"] = Environment.GetEnvironmentVariable("AGENTCONTROLLER_ENTRA_SCOPE")
        };

        return ConnectAgentControllerAsync(
            environment,
            static configuration => new AgentControllerTokenProvider(configuration),
            static options => new LTACEntityAdapter(options),
            cancellationToken);
    }

    internal static async Task<IEntityAdapter> ConnectAgentControllerAsync(
        IReadOnlyDictionary<string, string?> environment,
        Func<AgentControllerProviderConfiguration, AgentControllerTokenProvider> providerFactory,
        Func<LTACOptions, IEntityAdapter> adapterFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(adapterFactory);

        var configuration = AgentControllerProviderConfiguration.FromEnvironment(environment);
        var provider = providerFactory(configuration)
            ?? throw new InvalidOperationException("AgentController token provider creation failed.");
        try
        {
            var initial = await provider.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return adapterFactory(new LTACOptions
            {
                BaseUrl = initial.OpsBaseUrl.AbsoluteUri,
                BearerToken = initial.AccessToken,
                BearerTokenProvider = async ct =>
                {
                    var refreshed = await provider.AcquireAsync(ct).ConfigureAwait(false);
                    if (!initial.OpsBaseUrl.Equals(refreshed.OpsBaseUrl))
                    {
                        throw new InvalidOperationException(
                            "AgentController authorization endpoint changed; reconnect the vendor connection.");
                    }
                    return refreshed.AccessToken;
                },
                BearerTokenProviderOwner = provider
            });
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private static async Task<string> GetHaloAccessTokenAsync(string baseUrl, string clientId, string clientSecret, string scope, CancellationToken cancellationToken)
    {
        using var httpClient = VendorHttpClientFactory.Create(new Uri(baseUrl.TrimEnd('/') + "/"));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        });
        using var response = await httpClient.PostAsync("auth/token", content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HaloPSA token request failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("HaloPSA token response did not include an access token.");
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
