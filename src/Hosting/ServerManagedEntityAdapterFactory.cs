using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Adapters.BillCom;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.OrchestraMSP;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Hosting;

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
        "ORCHESTRA_BASE_URL",
        "ORCHESTRA_AUTHORITY",
        "ORCHESTRA_TENANT_ID",
        "ORCHESTRA_CLIENT_ID",
        "ORCHESTRA_RESOURCE",
        "ORCHESTRA_CLIENT_SECRET",
        "ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"
    ];

    private readonly IReadOnlyDictionary<string, string?> environment;
    private readonly Func<Dictionary<string, string>> createSecretConfiguration;
    private readonly Func<HttpClient> createOrchestraHttpClient;
    private readonly bool allowLoopbackHttp;

    public ServerManagedEntityAdapterFactory()
        : this(ReadCurrentEnvironment(), Environments.Production)
    {
    }

    public ServerManagedEntityAdapterFactory(IHostEnvironment hostEnvironment)
        : this(
            ReadCurrentEnvironment(),
            (hostEnvironment
             ?? throw new ArgumentNullException(nameof(hostEnvironment))).EnvironmentName)
    {
    }

    public ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment)
        : this(environment, Environments.Production)
    {
    }

    internal ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment,
        string environmentName)
        : this(
            environment,
            environmentName,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            static () => new HttpClient())
    {
    }

    internal ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment,
        Func<Dictionary<string, string>> createSecretConfiguration)
        : this(
            environment,
            Environments.Production,
            createSecretConfiguration,
            static () => new HttpClient())
    {
    }

    internal ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment,
        Func<HttpClient> createOrchestraHttpClient)
        : this(
            environment,
            Environments.Production,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            createOrchestraHttpClient)
    {
    }

    internal ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment,
        string environmentName,
        Func<HttpClient> createOrchestraHttpClient)
        : this(
            environment,
            environmentName,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            createOrchestraHttpClient)
    {
    }

    private ServerManagedEntityAdapterFactory(
        IReadOnlyDictionary<string, string?> environment,
        string environmentName,
        Func<Dictionary<string, string>> createSecretConfiguration,
        Func<HttpClient> createOrchestraHttpClient)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(createSecretConfiguration);
        ArgumentNullException.ThrowIfNull(createOrchestraHttpClient);
        this.environment = new Dictionary<string, string?>(
            environment,
            StringComparer.Ordinal);
        environment.TryGetValue(
            "ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA",
            out var allowInsecureValue);
        allowLoopbackHttp =
            EntitySyncProductionConfiguration.AllowLoopbackOrchestra(
                environmentName,
                allowInsecureValue);
        this.createSecretConfiguration = createSecretConfiguration;
        this.createOrchestraHttpClient = createOrchestraHttpClient;
    }

    public async Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        if (EntitySyncVendors.IsOrchestraMSP(normalized))
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OrchestraBaseUrl"] = Resolve(
                    profileSettings, "OrchestraBaseUrl", "ORCHESTRA_BASE_URL"),
                ["OrchestraAuthority"] = Resolve(
                    profileSettings, "OrchestraAuthority", "ORCHESTRA_AUTHORITY"),
                ["OrchestraTenantId"] = Resolve(
                    profileSettings, "OrchestraTenantId", "ORCHESTRA_TENANT_ID"),
                ["OrchestraClientId"] = Resolve(
                    profileSettings, "OrchestraClientId", "ORCHESTRA_CLIENT_ID"),
                ["OrchestraResource"] = Resolve(
                    profileSettings, "OrchestraResource", "ORCHESTRA_RESOURCE"),
                ["OrchestraClientSecret"] = Resolve(
                    profileSettings, "OrchestraClientSecret", "ORCHESTRA_CLIENT_SECRET")
            };
            try
            {
                return CreateOrchestraAdapter(settings, 1);
            }
            finally
            {
                settings.Clear();
            }
        }

        throw new InvalidOperationException("Unsupported vendor.");
    }

    public Task<IEntityAdapter> CreateDurableAsync(
        string vendor,
        IReadOnlyDictionary<string, JsonElement> publicConfiguration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        CancellationToken cancellationToken) =>
        CreateDurableAsync(
            vendor,
            publicConfiguration,
            secretConfiguration,
            ReadConnectionGeneration(publicConfiguration),
            cancellationToken);

    public async Task<IEntityAdapter> CreateDurableAsync(
        string vendor,
        IReadOnlyDictionary<string, JsonElement> publicConfiguration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publicConfiguration);
        ArgumentNullException.ThrowIfNull(secretConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new Dictionary<string, string>(
            publicConfiguration.Count + secretConfiguration.Count,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in publicConfiguration)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new ArgumentException(
                        "Public configuration keys are required.",
                        nameof(publicConfiguration));
                settings.Add(pair.Key, ConfigurationValue(pair.Key, pair.Value));
            }
            foreach (var pair in secretConfiguration)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value))
                    throw new ArgumentException(
                        "Secret configuration keys and values are required.",
                        nameof(secretConfiguration));
                settings.Add(pair.Key, pair.Value);
            }

            if (EntitySyncVendors.IsOrchestraMSP(vendor))
            {
                ValidateOrchestraSettings(settings);
                return CreateOrchestraAdapter(settings, connectionGeneration);
            }

            if (EntitySyncVendors.IsAgentController(vendor))
            {
                var durableEnvironment = new Dictionary<string, string?>(
                    StringComparer.Ordinal)
                {
                    ["AGENTCONTROLLER_AUTH_BASE_URL"] =
                        RequireSetting(settings, "AgentControllerAuthBaseUrl"),
                    ["AGENTCONTROLLER_ENTRA_TENANT_ID"] =
                        RequireSetting(settings, "AgentControllerEntraTenantId"),
                    ["AGENTCONTROLLER_ENTRA_CLIENT_ID"] =
                        RequireSetting(settings, "AgentControllerEntraClientId"),
                    ["AGENTCONTROLLER_ENTRA_CLIENT_SECRET"] =
                        RequireSetting(settings, "AgentControllerEntraClientSecret"),
                    ["AGENTCONTROLLER_ENTRA_SCOPE"] =
                        RequireSetting(settings, "AgentControllerEntraScope")
                };
                try
                {
                    return await ConnectAgentControllerAsync(
                        durableEnvironment,
                        static configuration => new AgentControllerTokenProvider(configuration),
                        static options => new LTACEntityAdapter(options),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    durableEnvironment.Clear();
                }
            }


            var definitionOnlyFactory = new ServerManagedEntityAdapterFactory(
                new Dictionary<string, string?>(StringComparer.Ordinal));
            return await definitionOnlyFactory.CreateAsync(
                vendor,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            settings.Clear();
        }
    }

    public ServerManagedConnectionConfiguration GetConnectionConfiguration(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings)
    {
        var publicConfiguration = new Dictionary<string, JsonElement>(
            StringComparer.OrdinalIgnoreCase);
        var secretConfiguration = createSecretConfiguration();
        try
        {
        var normalized = EntitySyncVendors.Normalize(vendor);
        var platformInstanceId = ResolvePlatformInstanceId(profileSettings);

        void AddPublic(string key, string value) =>
            publicConfiguration.Add(key, JsonSerializer.SerializeToElement(value));
        void AddOptionalPublic(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) AddPublic(key, value);
        }

        if (normalized.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            AddPublic("HaloBaseUrl", Resolve(profileSettings, "HaloBaseUrl", "HALO_BASE_URL"));
            AddPublic("HaloClientId", Resolve(profileSettings, "HaloClientId", "HALO_CLIENT_ID"));
            AddOptionalPublic("HaloScope", ResolveOptional(profileSettings, "HaloScope"));
            AddOptionalPublic(
                "HaloNetSuiteCustomerIdFieldId",
                ResolveOptional(
                    profileSettings,
                    "HaloNetSuiteCustomerIdFieldId",
                    "HALO_NETSUITE_CUSTOMER_ID_FIELD_ID"));
            AddOptionalPublic(
                "HaloNetSuiteCustomerNameField",
                ResolveOptional(
                    profileSettings,
                    "HaloNetSuiteCustomerNameField",
                    "HALO_NETSUITE_CUSTOMER_NAME_FIELD"));
            AddOptionalPublic(
                "HaloAccountManagerEmail",
                ResolveOptional(
                    profileSettings,
                    "HaloAccountManagerEmail",
                    "HALO_ACCOUNT_MANAGER_EMAIL"));
            AddOptionalPublic(
                "HaloNCentralIntegrationId",
                ResolveOptional(
                    profileSettings,
                    "HaloNCentralIntegrationId",
                    "HALO_NCENTRAL_INTEGRATION_ID"));
            secretConfiguration.Add(
                "HaloClientSecret",
                Resolve(profileSettings, "HaloClientSecret", "HALO_CLIENT_SECRET"));
        }
        else if (normalized.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            AddPublic("NetSuiteAccountId", Resolve(profileSettings, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID"));
            AddPublic("NetSuiteConsumerKey", Resolve(profileSettings, "NetSuiteConsumerKey", "NETSUITE_CONSUMER_KEY"));
            AddPublic("NetSuiteTokenId", Resolve(profileSettings, "NetSuiteTokenId", "NETSUITE_TOKEN_ID"));
            secretConfiguration.Add(
                "NetSuiteConsumerSecret",
                Resolve(profileSettings, "NetSuiteConsumerSecret", "NETSUITE_CONSUMER_SECRET"));
            secretConfiguration.Add(
                "NetSuiteTokenSecret",
                Resolve(profileSettings, "NetSuiteTokenSecret", "NETSUITE_TOKEN_SECRET"));
        }
        else if (normalized.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            AddPublic("NCentralBaseUrl", Resolve(profileSettings, "NCentralBaseUrl", "NCENTRAL_BASE_URL"));
            AddPublic("NCentralServiceOrgId", Resolve(profileSettings, "NCentralServiceOrgId", "NCENTRAL_SERVICE_ORG_ID"));
            AddOptionalPublic(
                "NCentralSoapUsername",
                ResolveOptional(profileSettings, "NCentralSoapUsername", "NCENTRAL_SOAP_USERNAME"));
            AddOptionalPublic(
                "NCentralSoapEndpointPath",
                ResolveOptional(
                    profileSettings,
                    "NCentralSoapEndpointPath",
                    "NCENTRAL_SOAP_ENDPOINT_PATH"));
            AddOptionalPublic(
                "NCentralSoapNamespace",
                ResolveOptional(
                    profileSettings,
                    "NCentralSoapNamespace",
                    "NCENTRAL_SOAP_NAMESPACE"));
            AddOptionalPublic(
                "NCentralHaloPsaIdPropertyLabel",
                ResolveOptional(
                    profileSettings,
                    "NCentralHaloPsaIdPropertyLabel",
                    "NCENTRAL_HALOPSA_ID_PROPERTY_LABEL"));
            AddOptionalPublic(
                "NCentralNetSuiteIdPropertyLabel",
                ResolveOptional(
                    profileSettings,
                    "NCentralNetSuiteIdPropertyLabel",
                    "NCENTRAL_NETSUITE_ID_PROPERTY_LABEL"));
            AddOptionalPublic(
                "NCentralNetSuiteNamePropertyLabel",
                ResolveOptional(
                    profileSettings,
                    "NCentralNetSuiteNamePropertyLabel",
                    "NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL"));
            secretConfiguration.Add(
                "NCentralUserApiToken",
                Resolve(profileSettings, "NCentralUserApiToken", "NCENTRAL_USER_API_TOKEN"));
            var soapPassword = ResolveOptional(
                profileSettings,
                "NCentralSoapPassword",
                "NCENTRAL_SOAP_PASSWORD");
            if (!string.IsNullOrWhiteSpace(soapPassword))
                secretConfiguration.Add("NCentralSoapPassword", soapPassword);
        }
        else if (EntitySyncVendors.IsAgentController(normalized))
        {
            AddPublic(
                "AgentControllerAuthBaseUrl",
                Resolve(profileSettings, "AgentControllerAuthBaseUrl", "AGENTCONTROLLER_AUTH_BASE_URL"));
            AddPublic(
                "AgentControllerEntraTenantId",
                Resolve(profileSettings, "AgentControllerEntraTenantId", "AGENTCONTROLLER_ENTRA_TENANT_ID"));
            AddPublic(
                "AgentControllerEntraClientId",
                Resolve(profileSettings, "AgentControllerEntraClientId", "AGENTCONTROLLER_ENTRA_CLIENT_ID"));
            AddPublic(
                "AgentControllerEntraScope",
                Resolve(profileSettings, "AgentControllerEntraScope", "AGENTCONTROLLER_ENTRA_SCOPE"));
            secretConfiguration.Add(
                "AgentControllerEntraClientSecret",
                Resolve(
                    profileSettings,
                    "AgentControllerEntraClientSecret",
                    "AGENTCONTROLLER_ENTRA_CLIENT_SECRET"));
        }
        else if (EntitySyncVendors.IsBillCom(normalized))
        {
            AddPublic(
                "BillComBaseUrl",
                ResolveOptional(profileSettings, "BillComBaseUrl", "BILLCOM_BASE_URL")
                    ?? "https://gateway.prod.bill.com/connect/v3/spend/custom-fields");
            AddOptionalPublic(
                "BillComClientFieldName",
                ResolveOptional(profileSettings, "BillComClientFieldName", "BILLCOM_CLIENT_FIELD_NAME"));
            secretConfiguration.Add(
                "BillComApiToken",
                Resolve(profileSettings, "BillComApiToken", "BILLCOM_API_TOKEN", "BILLSPEND_API_TOKEN"));
        }
        else if (EntitySyncVendors.IsOrchestraMSP(normalized))
        {
            var baseUrl = Resolve(
                profileSettings, "OrchestraBaseUrl", "ORCHESTRA_BASE_URL");
            var authority = Resolve(
                profileSettings, "OrchestraAuthority", "ORCHESTRA_AUTHORITY");
            var tenantId = Resolve(
                profileSettings, "OrchestraTenantId", "ORCHESTRA_TENANT_ID");
            var clientId = Resolve(
                profileSettings, "OrchestraClientId", "ORCHESTRA_CLIENT_ID");
            var resource = Resolve(
                profileSettings, "OrchestraResource", "ORCHESTRA_RESOURCE");
            var clientSecret = Resolve(
                profileSettings, "OrchestraClientSecret", "ORCHESTRA_CLIENT_SECRET");
            EntitySyncProductionConfiguration.ValidateOrchestraConnection(
                baseUrl,
                authority,
                tenantId,
                clientId,
                resource,
                clientSecret,
                allowLoopbackHttp);
            AddPublic("OrchestraBaseUrl", baseUrl);
            AddPublic("OrchestraAuthority", authority);
            AddPublic("OrchestraTenantId", tenantId);
            AddPublic("OrchestraClientId", clientId);
            AddPublic("OrchestraResource", resource);
            secretConfiguration.Add("OrchestraClientSecret", clientSecret);
        }
        else
        {
            throw new InvalidOperationException("Unsupported vendor.");
        }

        return new ServerManagedConnectionConfiguration(
            publicConfiguration,
            secretConfiguration,
            platformInstanceId);
        }
        catch
        {
            publicConfiguration.Clear();
            secretConfiguration.Clear();
            throw;
        }
    }

    private void ValidateOrchestraSettings(
        IReadOnlyDictionary<string, string> settings) =>
        EntitySyncProductionConfiguration.ValidateOrchestraConnection(
            RequireSetting(settings, "OrchestraBaseUrl"),
            RequireSetting(settings, "OrchestraAuthority"),
            RequireSetting(settings, "OrchestraTenantId"),
            RequireSetting(settings, "OrchestraClientId"),
            RequireSetting(settings, "OrchestraResource"),
            RequireSetting(settings, "OrchestraClientSecret"),
            allowLoopbackHttp);

    private OrchestraEntityAdapter CreateOrchestraAdapter(
        IReadOnlyDictionary<string, string> settings,
        long connectionGeneration)
    {
        if (connectionGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(connectionGeneration), connectionGeneration,
                "Connection generation must be positive.");
        var httpClient = createOrchestraHttpClient()
            ?? throw new InvalidOperationException(
                "The OrchestraMSP HTTP client factory returned no client.");
        byte[]? secretBytes = null;
        OrchestraTokenProvider? tokenProvider = null;
        try
        {
            secretBytes = Encoding.UTF8.GetBytes(
                RequireSetting(settings, "OrchestraClientSecret"));
            tokenProvider = new OrchestraTokenProvider(
                httpClient,
                new Uri(RequireSetting(settings, "OrchestraAuthority"), UriKind.Absolute),
                RequireSetting(settings, "OrchestraTenantId"),
                RequireSetting(settings, "OrchestraClientId"),
                secretBytes,
                RequireSetting(settings, "OrchestraResource"),
                connectionGeneration,
                TimeProvider.System);
            var directory = new OrchestraClientDirectoryClient(
                httpClient,
                tokenProvider,
                new Uri(RequireSetting(settings, "OrchestraBaseUrl"), UriKind.Absolute),
                disposeHttpClient: true);
            tokenProvider = null;
            return new OrchestraEntityAdapter(directory);
        }
        catch
        {
            tokenProvider?.Dispose();
            httpClient.Dispose();
            throw;
        }
        finally
        {
            if (secretBytes is not null)
                CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static long ReadConnectionGeneration(
        IReadOnlyDictionary<string, JsonElement> publicConfiguration)
    {
        ArgumentNullException.ThrowIfNull(publicConfiguration);
        if (!publicConfiguration.TryGetValue(
                "OrchestraConnectionGeneration", out var configured))
            return 1;
        if (configured.ValueKind == JsonValueKind.Number
            && configured.TryGetInt64(out var number)
            && number > 0)
            return number;
        if (configured.ValueKind == JsonValueKind.String
            && long.TryParse(
                configured.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number)
            && number > 0)
            return number;
        throw new ArgumentException(
            "Orchestra connection generation must be a positive integer.",
            nameof(publicConfiguration));
    }

    private static string ConfigurationValue(string key, JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                value.GetRawText(),
            _ => throw new ArgumentException(
                $"Public configuration '{key}' must be a string, number, or boolean.",
                nameof(value))
        };

    private static string RequireSetting(
        IReadOnlyDictionary<string, string> settings,
        string key)
    {
        if (settings.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new InvalidOperationException(
            $"Stored connection configuration is missing required setting '{key}'.");
    }

    public void ValidateNetSuiteHaloFixedRouteConfiguration()
    {
        _ = CreateNetSuiteOptions(null);
        _ = Resolve(null, "HaloClientId", "HALO_CLIENT_ID");
        _ = Resolve(null, "HaloClientSecret", "HALO_CLIENT_SECRET");
        _ = CreateHaloOptions(null, "startup-configuration-validation");
        _ = GetNetSuiteHaloChangeStateScope();
    }

    public string GetNetSuiteHaloChangeStateScope()
    {
        var accountId = Resolve(null, "NetSuiteAccountId", "NETSUITE_ACCOUNT_ID")
            .Trim()
            .ToUpperInvariant();
        var haloBaseUrl = RequireRouteIdentityHttpsBaseUrl(
            Resolve(null, "HaloBaseUrl", "HALO_BASE_URL"));
        var canonical = $"netsuite|{accountId}|customer|netsuite|halopsa|{haloBaseUrl}|client|halopsa";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
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

    private Guid? ResolvePlatformInstanceId(
        IReadOnlyDictionary<string, string>? profileSettings)
    {
        var value =
            profileSettings?.TryGetValue(
                "EntitySyncPlatformInstanceId", out var configured) == true
            && !string.IsNullOrWhiteSpace(configured)
                ? configured
                : null;
        if (value is null) return null;
        if (!Guid.TryParse(value, out var platformInstanceId)
            || platformInstanceId == Guid.Empty)
            throw new InvalidOperationException(
                "EntitySync platform instance ID must be a non-empty UUID.");
        return platformInstanceId;
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
            || (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     && IPAddress.TryParse(uri.Host, out var address)
                     && IPAddress.IsLoopback(address))))
        {
            throw new InvalidOperationException(
                $"{vendor} server configuration must use HTTPS " +
                "except for an explicit loopback test endpoint.");
        }

        return uri.AbsoluteUri;
    }

    private static string RequireRouteIdentityHttpsBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "HaloPSA route identity must use an absolute HTTPS base URL without user info, a query, or a fragment.");
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
