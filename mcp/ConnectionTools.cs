using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.BillCom;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class ConnectionTools
{
    [McpServerTool]
    [Description("Connect to a vendor using a named EntitySync profile or the default profile. Profile credentials are DPAPI-protected and never returned through MCP. Environment variables are used only when no matching profile exists. Do not pass secrets as tool arguments.")]
    public static string ConnectVendor(
        SyncSession session,
        [Description("Vendor name: HaloPSA, NetSuite, NCentral, or Bill.com")] string vendor,
        [Description("EntitySync profile name. Omit to use the default profile.")] string? profileName = null,
        [Description("Legacy direct HaloPSA base URL. Prefer profileName or the default profile.")] string? haloBaseUrl = null,
        [Description("Legacy direct HaloPSA client ID. Prefer profileName or the default profile.")] string? haloClientId = null,
        [Description("Legacy direct HaloPSA client secret. Do not pass secrets through chat; use an EntitySync profile.")] string? haloClientSecret = null,
        [Description("Legacy direct NetSuite account ID. Prefer profileName or the default profile.")] string? netSuiteAccountId = null,
        [Description("Legacy direct NetSuite consumer key. Do not pass secrets through chat; use an EntitySync profile.")] string? netSuiteConsumerKey = null,
        [Description("Legacy direct NetSuite consumer secret. Do not pass secrets through chat; use an EntitySync profile.")] string? netSuiteConsumerSecret = null,
        [Description("Legacy direct NetSuite token ID. Do not pass secrets through chat; use an EntitySync profile.")] string? netSuiteTokenId = null,
        [Description("Legacy direct NetSuite token secret. Do not pass secrets through chat; use an EntitySync profile.")] string? netSuiteTokenSecret = null,
        [Description("Legacy direct NCentral base URL. Prefer profileName or the default profile.")] string? ncentralBaseUrl = null,
        [Description("Legacy direct NCentral API token. Do not pass secrets through chat; use an EntitySync profile.")] string? ncentralUserApiToken = null,
        [Description("Legacy direct NCentral service organization ID. Prefer profileName or the default profile.")] string? ncentralServiceOrgId = null,
        [Description("Legacy direct Bill.com API token. Do not pass secrets through chat; use an EntitySync profile.")] string? billComApiToken = null)
    {
        try
        {
            var normalized = EntitySyncVendors.Normalize(vendor);
            var profile = FindProfile(normalized, profileName);

            if (normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = Resolve(haloBaseUrl, profile, "HaloBaseUrl", "HALO_BASE_URL");
                var clientId = Resolve(haloClientId, profile, "HaloClientId", "HALO_CLIENT_ID");
                var clientSecret = Resolve(haloClientSecret, profile, "HaloClientSecret", "HALO_CLIENT_SECRET");
                var scope = ResolveOptional(profile, "HaloScope") ?? "all";
                var token = GetHaloAccessToken(baseUrl, clientId, clientSecret, scope);
                var adapter = new HaloEntityAdapter(new HaloOptions
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
                ConnectionRegistry.Set(adapter);
            }
            else if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            {
                var adapter = new NetSuiteEntityAdapter(new NetSuiteOptions
                {
                    AccountId = Resolve(netSuiteAccountId, profile, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID"),
                    ConsumerKey = Resolve(netSuiteConsumerKey, profile, "NetSuiteConsumerKey", "NETSUITE_CONSUMER_KEY"),
                    ConsumerSecret = Resolve(netSuiteConsumerSecret, profile, "NetSuiteConsumerSecret", "NETSUITE_CONSUMER_SECRET"),
                    TokenId = Resolve(netSuiteTokenId, profile, "NetSuiteTokenId", "NETSUITE_TOKEN_ID"),
                    TokenSecret = Resolve(netSuiteTokenSecret, profile, "NetSuiteTokenSecret", "NETSUITE_TOKEN_SECRET")
                });
                ConnectionRegistry.Set(adapter);
            }
            else if (normalized.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
            {
                var adapter = new NCentralEntityAdapter(new NCentralOptions
                {
                    BaseUrl = Resolve(ncentralBaseUrl, profile, "NCentralBaseUrl", "NCENTRAL_BASE_URL"),
                    UserApiToken = Resolve(ncentralUserApiToken, profile, "NCentralUserApiToken", "NCENTRAL_USER_API_TOKEN"),
                    ServiceOrgId = Resolve(ncentralServiceOrgId, profile, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID"),
                    SoapUsername = ResolveOptional(profile, "NCentralSoapUsername", "NCENTRAL_SOAP_USERNAME") ?? string.Empty,
                    SoapPassword = ResolveOptional(profile, "NCentralSoapPassword", "NCENTRAL_SOAP_PASSWORD") ?? string.Empty,
                    SoapEndpointPath = ResolveOptional(profile, "NCentralSoapEndpointPath", "NCENTRAL_SOAP_ENDPOINT_PATH") ?? "dms2/services2/ServerEI2",
                    SoapNamespace = ResolveOptional(profile, "NCentralSoapNamespace", "NCENTRAL_SOAP_NAMESPACE") ?? "http://ei2.nobj.nable.com/",
                    HaloPsaIdPropertyLabel = ResolveOptional(profile, "NCentralHaloPsaIdPropertyLabel", "NCENTRAL_HALOPSA_ID_PROPERTY_LABEL") ?? "HaloPSA Client ID",
                    NetSuiteIdPropertyLabel = ResolveOptional(profile, "NCentralNetSuiteIdPropertyLabel", "NCENTRAL_NETSUITE_ID_PROPERTY_LABEL") ?? "NetSuite Customer ID",
                    NetSuiteNamePropertyLabel = ResolveOptional(profile, "NCentralNetSuiteNamePropertyLabel", "NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL") ?? "NetSuite Customer Name"
                });
                ConnectionRegistry.Set(adapter);
            }
            else if (EntitySyncVendors.IsBillCom(normalized))
            {
                var adapter = new BillComEntityAdapter(new BillComOptions
                {
                    BaseUrl = ResolveOptional(profile, "BillComBaseUrl", "BILLCOM_BASE_URL") ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields",
                    ApiToken = Resolve(billComApiToken, profile, "BillComApiToken", "BILLCOM_API_TOKEN", "BILLSPEND_API_TOKEN"),
                    ClientFieldName = ResolveOptional(profile, "BillComClientFieldName", "BILLCOM_CLIENT_FIELD_NAME") ?? "Client"
                });
                ConnectionRegistry.Set(adapter);
            }
            else
            {
                return JsonSerializer.Serialize(new { success = false, error = $"Unsupported vendor '{vendor}'. Use HaloPSA, NetSuite, NCentral, or Bill.com." });
            }

            return JsonSerializer.Serialize(new { success = true, vendor = normalized, profile = profile?.Name, message = $"Connected to {normalized}." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("List EntitySync connection profiles without returning paths or decrypted settings.")]
    public static string ListProfiles()
    {
        try
        {
            var profiles = EntitySyncProfileStore.ListProfiles().Select(profile => new
            {
                profile.Name,
                profile.IsDefault,
                profile.Vendors
            });
            return JsonSerializer.Serialize(new { success = true, profiles });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Test connectivity to a connected vendor adapter.")]
    public static async Task<string> TestConnection(
        [Description("Vendor name to test")] string vendor)
    {
        try
        {
            var normalized = EntitySyncVendors.Normalize(vendor);
            var adapter = ConnectionRegistry.Get(normalized);
            var result = await adapter.TestConnectionAsync(CancellationToken.None);
            return JsonSerializer.Serialize(new { success = true, vendor = normalized, connected = result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("List all currently connected vendor adapters.")]
    public static string ListConnections(SyncSession session)
    {
        return JsonSerializer.Serialize(new { vendors = session.ConnectedVendors });
    }

    [McpServerTool]
    [Description("Read entities from a connected vendor. Returns ExternalEntity records as JSON.")]
    public static async Task<string> GetEntities(
        [Description("Vendor to read from")] string vendor,
        [Description("Entity type: HaloPSA=Client/Site, NetSuite=Customer, NCentral=Customer/Site, Bill.com=Client")] string entityType = "Customer",
        [Description("Optional name search filter")] string? search = null,
        [Description("Include inactive entities")] bool includeInactive = false,
        [Description("Max entities to return (0 = all)")] int count = 0)
    {
        try
        {
            var normalized = EntitySyncVendors.Normalize(vendor);
            var adapter = ConnectionRegistry.Get(normalized);
            var query = new EntityQuery
            {
                EntityType = entityType,
                Search = search,
                IncludeInactive = includeInactive,
                FullObjects = false
            };
            if (count > 0) query.Count = count;

            var entities = await adapter.GetEntitiesAsync(query, CancellationToken.None);
            var result = entities.Select(e => new
            {
                e.Vendor,
                e.EntityType,
                e.Id,
                e.Name,
                e.Email,
                e.Phone,
                e.Website,
                e.IsActive,
                externalIds = e.ExternalIds,
                customFields = e.CustomFields
            });
            return JsonSerializer.Serialize(new { success = true, count = entities.Count, entities = result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
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

    private static string Resolve(string? value, ResolvedVendorProfile? profile, string profileKey, params string[] envVars)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var profileValue = ResolveOptional(profile, profileKey);
        if (!string.IsNullOrWhiteSpace(profileValue)) return profileValue;
        foreach (var env in envVars)
        {
            var envValue = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(envValue)) return envValue;
        }
        throw new InvalidOperationException($"Missing required value. Pass the parameter or set {string.Join(" or ", envVars)}.");
    }

    private static string? ResolveOptional(ResolvedVendorProfile? profile, string profileKey, params string[] envVars)
    {
        if (profile?.Settings.TryGetValue(profileKey, out var profileValue) == true && !string.IsNullOrWhiteSpace(profileValue)) return profileValue;
        foreach (var env in envVars)
        {
            var envValue = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(envValue)) return envValue;
        }
        return null;
    }

    private static int ResolveInt(ResolvedVendorProfile? profile, string profileKey, int defaultValue, params string[] envVars)
    {
        var value = ResolveOptional(profile, profileKey, envVars);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) return result;
        throw new InvalidOperationException($"EntitySync profile setting '{profileKey}' must be an integer.");
    }

    private static string GetHaloAccessToken(string baseUrl, string clientId, string clientSecret, string scope)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        });
        using var response = httpClient.PostAsync("auth/token", content).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HaloPSA token request failed with HTTP {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("HaloPSA token response did not include access_token.");
    }

    private sealed record ResolvedVendorProfile(string Name, IReadOnlyDictionary<string, string> Settings);
}
