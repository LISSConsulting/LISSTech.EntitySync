using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Adapters.BillCom;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Adapters.SophosCentral;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Hosting;

public sealed class ServerManagedEntityAdapterFactory : IServerManagedEntityAdapterFactory
{
    private static readonly string[] EnvironmentVariableNames =
    [
        "HALO_BASE_URL",
        "HALO_CLIENT_ID",
        "HALO_CLIENT_SECRET",
        "HALO_NETSUITE_CUSTOMER_ID_FIELD_ID",
        "HALO_NETSUITE_CUSTOMER_NAME_FIELD",
        "HALO_ACCOUNT_MANAGER_EMAIL",
        "HALO_NCENTRAL_INTEGRATION_ID",
        "NETSUITE_ACCOUNT_ID",
        "NETSUITE_CONSUMER_KEY",
        "NETSUITE_CONSUMER_SECRET",
        "NETSUITE_TOKEN_ID",
        "NETSUITE_TOKEN_SECRET",
        "NCENTRAL_BASE_URL",
        "NCENTRAL_USER_API_TOKEN",
        "NCENTRAL_SERVICE_ORG_ID",
        "NCENTRAL_SOAP_USERNAME",
        "NCENTRAL_SOAP_PASSWORD",
        "NCENTRAL_SOAP_ENDPOINT_PATH",
        "NCENTRAL_SOAP_NAMESPACE",
        "NCENTRAL_HALOPSA_ID_PROPERTY_LABEL",
        "NCENTRAL_NETSUITE_ID_PROPERTY_LABEL",
        "NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL",
        "AGENTCONTROLLER_AUTH_BASE_URL",
        "AGENTCONTROLLER_ENTRA_TENANT_ID",
        "AGENTCONTROLLER_ENTRA_CLIENT_ID",
        "AGENTCONTROLLER_ENTRA_CLIENT_SECRET",
        "AGENTCONTROLLER_ENTRA_SCOPE",
        "BILLCOM_BASE_URL",
        "BILLCOM_API_TOKEN",
        "BILLSPEND_API_TOKEN",
        "BILLCOM_CLIENT_FIELD_NAME",
        "SOPHOS_CENTRAL_CLIENT_ID",
        "SOPHOS_CENTRAL_CLIENT_SECRET",
        "SOPHOS_CENTRAL_DEFAULT_DATA_GEOGRAPHY",
        "SOPHOS_CENTRAL_DEFAULT_DATA_REGION",
        "SOPHOS_CENTRAL_DEFAULT_BILLING_TYPE"
    ];

    private readonly IReadOnlyDictionary<string, string?> environment;

    public ServerManagedEntityAdapterFactory()
        : this(ReadCurrentEnvironment())
    {
    }

    public ServerManagedEntityAdapterFactory(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.environment = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
    }

    public async Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken)
    {
        var normalized = EntitySyncVendors.Normalize(vendor);
        if (normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = RequireHttps(Resolve(profileSettings, "HaloBaseUrl", "HALO_BASE_URL"), normalized);
            var accessToken = await GetHaloAccessTokenAsync(
                baseUrl,
                Resolve(profileSettings, "HaloClientId", "HALO_CLIENT_ID"),
                Resolve(profileSettings, "HaloClientSecret", "HALO_CLIENT_SECRET"),
                ResolveOptional(profileSettings, "HaloScope") ?? "all",
                cancellationToken).ConfigureAwait(false);
            return new HaloEntityAdapter(CreateHaloOptions(profileSettings, accessToken));
        }

        if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            return new NetSuiteEntityAdapter(CreateNetSuiteOptions(profileSettings));
        }

        if (normalized.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            return new NCentralEntityAdapter(new NCentralOptions
            {
                BaseUrl = RequireHttps(Resolve(profileSettings, "NCentralBaseUrl", "NCENTRAL_BASE_URL"), normalized),
                UserApiToken = Resolve(profileSettings, "NCentralUserApiToken", "NCENTRAL_USER_API_TOKEN"),
                ServiceOrgId = Resolve(profileSettings, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID"),
                SoapUsername = ResolveOptional(profileSettings, "NCentralSoapUsername", "NCENTRAL_SOAP_USERNAME") ?? string.Empty,
                SoapPassword = ResolveOptional(profileSettings, "NCentralSoapPassword", "NCENTRAL_SOAP_PASSWORD") ?? string.Empty,
                SoapEndpointPath = ResolveOptional(profileSettings, "NCentralSoapEndpointPath", "NCENTRAL_SOAP_ENDPOINT_PATH") ?? "dms2/services2/ServerEI2",
                SoapNamespace = ResolveOptional(profileSettings, "NCentralSoapNamespace", "NCENTRAL_SOAP_NAMESPACE") ?? "http://ei2.nobj.nable.com/",
                HaloPsaIdPropertyLabel = ResolveOptional(profileSettings, "NCentralHaloPsaIdPropertyLabel", "NCENTRAL_HALOPSA_ID_PROPERTY_LABEL") ?? "HaloPSA Client ID",
                NetSuiteIdPropertyLabel = ResolveOptional(profileSettings, "NCentralNetSuiteIdPropertyLabel", "NCENTRAL_NETSUITE_ID_PROPERTY_LABEL") ?? "NetSuite Customer ID",
                NetSuiteNamePropertyLabel = ResolveOptional(profileSettings, "NCentralNetSuiteNamePropertyLabel", "NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL") ?? "NetSuite Customer Name"
            });
        }

        if (EntitySyncVendors.IsAgentController(normalized))
        {
            if (profileSettings is not null)
            {
                throw new InvalidOperationException(
                    "AgentController profiles are managed through the local EntitySync profile store. The MCP server does not accept profile credentials.");
            }

            return await ConnectAgentControllerAsync(
                environment,
                static configuration => new AgentControllerTokenProvider(configuration),
                static options => new LTACEntityAdapter(options),
                cancellationToken).ConfigureAwait(false);
        }

        if (EntitySyncVendors.IsBillCom(normalized))
        {
            return new BillComEntityAdapter(new BillComOptions
            {
                BaseUrl = RequireHttps(
                    ResolveOptional(profileSettings, "BillComBaseUrl", "BILLCOM_BASE_URL")
                        ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields",
                    normalized),
                ApiToken = Resolve(profileSettings, "BillComApiToken", "BILLCOM_API_TOKEN", "BILLSPEND_API_TOKEN"),
                ClientFieldName = ResolveOptional(profileSettings, "BillComClientFieldName", "BILLCOM_CLIENT_FIELD_NAME") ?? "Client"
            });
        }
        if (EntitySyncVendors.IsSophosCentral(normalized))
        {
            return new SophosCentralEntityAdapter(new SophosCentralOptions
            {
                ClientId = Resolve(profileSettings, "SophosCentralClientId", "SOPHOS_CENTRAL_CLIENT_ID"),
                ClientSecret = Resolve(profileSettings, "SophosCentralClientSecret", "SOPHOS_CENTRAL_CLIENT_SECRET"),
                DefaultDataGeography = ResolveOptional(profileSettings, "SophosCentralDefaultDataGeography", "SOPHOS_CENTRAL_DEFAULT_DATA_GEOGRAPHY"),
                DefaultDataRegion = ResolveOptional(profileSettings, "SophosCentralDefaultDataRegion", "SOPHOS_CENTRAL_DEFAULT_DATA_REGION"),
                DefaultBillingType = ResolveOptional(profileSettings, "SophosCentralDefaultBillingType", "SOPHOS_CENTRAL_DEFAULT_BILLING_TYPE")
            });
        }


        throw new InvalidOperationException("Unsupported vendor.");
    }

    public void ValidateConfiguration(IEnumerable<string> vendors)
    {
        ArgumentNullException.ThrowIfNull(vendors);
        foreach (var vendor in vendors.Select(EntitySyncVendors.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            {
                _ = CreateNetSuiteOptions(null);
                continue;
            }
            if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                _ = Resolve(null, "HaloClientId", "HALO_CLIENT_ID");
                _ = Resolve(null, "HaloClientSecret", "HALO_CLIENT_SECRET");
                _ = CreateHaloOptions(null, "startup-configuration-validation");
                var nCentralIntegrationId = Resolve(
                    null,
                    "HaloNCentralIntegrationId",
                    "HALO_NCENTRAL_INTEGRATION_ID");
                if (!int.TryParse(nCentralIntegrationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIntegrationId)
                    || parsedIntegrationId <= 0)
                {
                    throw new InvalidOperationException(
                        "Server configuration 'HALO_NCENTRAL_INTEGRATION_ID' must be a positive integer.");
                }
                continue;
            }
            if (vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
            {
                _ = RequireHttps(Resolve(null, "NCentralBaseUrl", "NCENTRAL_BASE_URL"), vendor);
                _ = Resolve(null, "NCentralUserApiToken", "NCENTRAL_USER_API_TOKEN");
                _ = Resolve(null, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID");
                _ = Resolve(null, "NCentralSoapUsername", "NCENTRAL_SOAP_USERNAME");
                _ = Resolve(null, "NCentralSoapPassword", "NCENTRAL_SOAP_PASSWORD");
                continue;
            }
            if (EntitySyncVendors.IsBillCom(vendor))
            {
                _ = RequireHttps(
                    ResolveOptional(null, "BillComBaseUrl", "BILLCOM_BASE_URL")
                        ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields",
                    vendor);
                _ = Resolve(null, "BillComApiToken", "BILLCOM_API_TOKEN", "BILLSPEND_API_TOKEN");
                continue;
            }
            if (EntitySyncVendors.IsSophosCentral(vendor))
            {
                _ = Resolve(null, "SophosCentralClientId", "SOPHOS_CENTRAL_CLIENT_ID");
                _ = Resolve(null, "SophosCentralClientSecret", "SOPHOS_CENTRAL_CLIENT_SECRET");
                continue;
            }

            throw new InvalidOperationException($"Unsupported scheduled vendor '{vendor}'.");
        }
    }

    public string GetChangeStateScope(
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType)
    {
        var normalizedSource = EntitySyncVendors.Normalize(sourceVendor);
        var normalizedTarget = EntitySyncVendors.Normalize(targetVendor);
        var canonical = string.Join(
            "|",
            normalizedSource.ToLowerInvariant(),
            GetVendorRouteIdentity(normalizedSource),
            sourceConnectionId.Trim().ToLowerInvariant(),
            sourceEntityType.Trim().ToLowerInvariant(),
            normalizedTarget.ToLowerInvariant(),
            GetVendorRouteIdentity(normalizedTarget),
            targetConnectionId.Trim().ToLowerInvariant(),
            targetEntityType.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private string GetVendorRouteIdentity(string vendor)
    {
        if (vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            return Resolve(null, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID").Trim().ToUpperInvariant();
        if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            return RequireRouteIdentityHttpsBaseUrl(Resolve(null, "HaloBaseUrl", "HALO_BASE_URL"), vendor);
        if (vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
            return string.Join(
                "|",
                RequireRouteIdentityHttpsBaseUrl(Resolve(null, "NCentralBaseUrl", "NCENTRAL_BASE_URL"), vendor),
                Resolve(null, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID").Trim());
        if (EntitySyncVendors.IsBillCom(vendor))
            return string.Join(
                "|",
                RequireRouteIdentityHttpsBaseUrl(
                    ResolveOptional(null, "BillComBaseUrl", "BILLCOM_BASE_URL")
                        ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields",
                    vendor),
                ResolveOptional(null, "BillComClientFieldName", "BILLCOM_CLIENT_FIELD_NAME")?.Trim() ?? "Client");
        if (EntitySyncVendors.IsSophosCentral(vendor))
            return Resolve(null, "SophosCentralClientId", "SOPHOS_CENTRAL_CLIENT_ID").Trim();

        throw new InvalidOperationException($"Unsupported scheduled vendor '{vendor}'.");
    }

    internal NetSuiteOptions CreateNetSuiteOptions(IReadOnlyDictionary<string, string>? profileSettings) => new()
    {
        AccountId = Resolve(profileSettings, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID"),
        ConsumerKey = Resolve(profileSettings, "NetSuiteConsumerKey", "NETSUITE_CONSUMER_KEY"),
        ConsumerSecret = Resolve(profileSettings, "NetSuiteConsumerSecret", "NETSUITE_CONSUMER_SECRET"),
        TokenId = Resolve(profileSettings, "NetSuiteTokenId", "NETSUITE_TOKEN_ID"),
        TokenSecret = Resolve(profileSettings, "NetSuiteTokenSecret", "NETSUITE_TOKEN_SECRET")
    };

    internal HaloOptions CreateHaloOptions(
        IReadOnlyDictionary<string, string>? profileSettings,
        string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("HaloPSA token response did not include an access token.");
        }

        return new HaloOptions
        {
            BaseUrl = RequireHttps(Resolve(profileSettings, "HaloBaseUrl", "HALO_BASE_URL"), "HaloPSA"),
            AccessToken = accessToken,
            TopLevelId = ResolveInt(profileSettings, "HaloTopLevelId", 1),
            DefaultColour = ResolveOptional(profileSettings, "HaloDefaultColour") ?? "#E83C4A",
            NetSuiteCustomerIdField = ResolveOptional(profileSettings, "HaloNetSuiteCustomerIdField") ?? "CFNetSuiteCustomerID",
            NetSuiteCustomerIdFieldId = ResolveOptional(profileSettings, "HaloNetSuiteCustomerIdFieldId", "HALO_NETSUITE_CUSTOMER_ID_FIELD_ID") ?? string.Empty,
            NetSuiteCustomerNameField = ResolveOptional(profileSettings, "HaloNetSuiteCustomerNameField", "HALO_NETSUITE_CUSTOMER_NAME_FIELD") ?? "CFNetSuiteCustomerName",
            CustomerRelationshipId = ResolveInt(profileSettings, "HaloCustomerRelationshipId", 0),
            CustomerRelationshipName = ResolveOptional(profileSettings, "HaloCustomerRelationshipName") ?? string.Empty,
            CustomerTypeId = ResolveInt(profileSettings, "HaloCustomerTypeId", 0),
            CustomerTypeName = ResolveOptional(profileSettings, "HaloCustomerTypeName") ?? string.Empty,
            AccountManagerEmail = ResolveOptional(profileSettings, "HaloAccountManagerEmail", "HALO_ACCOUNT_MANAGER_EMAIL"),
            AccountManagerField = ResolveOptional(profileSettings, "HaloAccountManagerField") ?? "CFassignedtam",
            NCentralIntegrationId = ResolveInt(profileSettings, "HaloNCentralIntegrationId", 0, "HALO_NCENTRAL_INTEGRATION_ID")
        };
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

    public override string ToString() => nameof(ServerManagedEntityAdapterFactory);

    private string Resolve(
        IReadOnlyDictionary<string, string>? profileSettings,
        string profileKey,
        params string[] environmentVariables)
    {
        var value = ResolveOptional(profileSettings, profileKey, environmentVariables);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException(
            $"Missing required server configuration: {string.Join(" or ", environmentVariables)}.");
    }

    private string? ResolveOptional(
        IReadOnlyDictionary<string, string>? profileSettings,
        string profileKey,
        params string[] environmentVariables)
    {
        if (profileSettings?.TryGetValue(profileKey, out var profileValue) == true
            && !string.IsNullOrWhiteSpace(profileValue))
        {
            return profileValue;
        }

        foreach (var variableName in environmentVariables)
        {
            if (environment.TryGetValue(variableName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private int ResolveInt(
        IReadOnlyDictionary<string, string>? profileSettings,
        string profileKey,
        int defaultValue,
        params string[] environmentVariables)
    {
        var value = ResolveOptional(profileSettings, profileKey, environmentVariables);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) return result;
        throw new InvalidOperationException($"Server configuration '{profileKey}' must be an integer.");
    }

    private static string RequireHttps(string value, string vendor)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{vendor} server configuration must use an absolute HTTPS URL.");
        }

        return uri.AbsoluteUri;
    }

    private static string RequireRouteIdentityHttpsBaseUrl(string value, string vendor)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{vendor} route identity must use an absolute HTTPS base URL without user info, a query, or a fragment.");
        }

        return uri.AbsoluteUri;
    }

    private static async Task<string> GetHaloAccessTokenAsync(
        string baseUrl,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken)
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
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"HaloPSA token request failed with HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException(
                "HaloPSA token response did not include an access token.");
    }

    private static IReadOnlyDictionary<string, string?> ReadCurrentEnvironment()
    {
        var values = new Dictionary<string, string?>(EnvironmentVariableNames.Length, StringComparer.Ordinal);
        foreach (var variableName in EnvironmentVariableNames)
        {
            values[variableName] = Environment.GetEnvironmentVariable(variableName);
        }
        return values;
    }
}
