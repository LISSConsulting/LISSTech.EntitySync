using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommunications.Connect, "EntitySyncVendor", DefaultParameterSetName = "HaloPSA")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class ConnectEntitySyncVendorCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, ParameterSetName = "HaloPSA")]
    [Parameter(Mandatory = true, ParameterSetName = "NetSuite")]
    [Parameter(Mandatory = true, ParameterSetName = "NCentral")]
    [Parameter(Mandatory = true, ParameterSetName = "LCAT")]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral", "LCAT", "LTAC")]
    public string Vendor { get; set; } = string.Empty;

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    /// <summary>
    /// LCAT connections may be registered with the `LTAC` alias, but every plan and artifact
    /// produced afterwards must still identify the vendor as `LCAT` (spec FR-002).
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) =>
        vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase) ? "LCAT" : vendor;

    public object? GetDynamicParameters()
    {
        Vendor = NormalizeVendorAlias(Vendor);
        dynamicParameters = new RuntimeDefinedParameterDictionary();
        if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            AddDynamicParameter<string>("HaloBaseUrl");
            AddDynamicParameter<string>("HaloClientId");
            AddDynamicParameter<string>("HaloClientSecret");
            AddDynamicParameter<string>("HaloScope", "all");
            AddDynamicParameter<int>("HaloTopLevelId", 1);
            AddDynamicParameter<string>("HaloDefaultColour", "#E83C4A");
            AddDynamicParameter<string>("HaloNetSuiteCustomerIdField", "CFNetSuiteCustomerID");
            AddDynamicParameter<string>("HaloNetSuiteCustomerIdFieldId");
            AddDynamicParameter<string>("HaloNetSuiteCustomerNameField", "CFNetSuiteCustomerName");
            AddDynamicParameter<int>("HaloCustomerRelationshipId", 0);
            AddDynamicParameter<string>("HaloCustomerRelationshipName");
            AddDynamicParameter<int>("HaloCustomerTypeId", 0);
            AddDynamicParameter<string>("HaloCustomerTypeName");
            AddDynamicParameter<string>("HaloAccountManagerEmail");
            AddDynamicParameter<string>("HaloAccountManagerField", "CFassignedtam");
            AddDynamicParameter<int>("HaloNCentralIntegrationId", 0);
        }
        else if (Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            AddDynamicParameter<string>("NetSuiteAccountId");
            AddDynamicParameter<string>("NetSuiteConsumerKey");
            AddDynamicParameter<string>("NetSuiteConsumerSecret");
            AddDynamicParameter<string>("NetSuiteTokenId");
            AddDynamicParameter<string>("NetSuiteTokenSecret");
        }
        else if (Vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            AddDynamicParameter<string>("NCentralBaseUrl");
            AddDynamicParameter<string>("NCentralUserApiToken");
            AddDynamicParameter<string>("NCentralServiceOrgId");
            AddDynamicParameter<string>("NCentralSoapUsername");
            AddDynamicParameter<string>("NCentralSoapPassword");
            AddDynamicParameter<string>("NCentralSoapEndpointPath", "dms2/services2/ServerEI2");
            AddDynamicParameter<string>("NCentralSoapNamespace", "http://ei2.nobj.nable.com/");
            AddDynamicParameter<string>("NCentralHaloPsaIdPropertyLabel", "HaloPSA Client ID");
            AddDynamicParameter<string>("NCentralNetSuiteIdPropertyLabel", "NetSuite Customer ID");
            AddDynamicParameter<string>("NCentralNetSuiteNamePropertyLabel", "NetSuite Customer Name");
        }

        return dynamicParameters;
    }

    protected override void EndProcessing()
    {
        try
        {
            Vendor = NormalizeVendorAlias(Vendor);

            if (Vendor.Equals("LCAT", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotImplementedException("LCAT connection support is implemented in a later EntitySync task.");
            }

            if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                var haloScope = DynamicValue("HaloScope", "all");
                var haloTopLevelId = DynamicValue("HaloTopLevelId", 1);
                var haloDefaultColour = DynamicValue("HaloDefaultColour", "#E83C4A");
                var haloNetSuiteCustomerIdField = DynamicValue("HaloNetSuiteCustomerIdField", "CFNetSuiteCustomerID");
                var haloNetSuiteCustomerIdFieldId = DynamicValue<string?>("HaloNetSuiteCustomerIdFieldId", null) ?? Environment.GetEnvironmentVariable("HALO_NETSUITE_CUSTOMER_ID_FIELD_ID") ?? string.Empty;
                var haloNetSuiteCustomerNameField = DynamicValue<string?>("HaloNetSuiteCustomerNameField", null) ?? Environment.GetEnvironmentVariable("HALO_NETSUITE_CUSTOMER_NAME_FIELD") ?? "CFNetSuiteCustomerName";
                var haloCustomerRelationshipId = DynamicValue("HaloCustomerRelationshipId", 0);
                var haloCustomerRelationshipName = DynamicValue("HaloCustomerRelationshipName", string.Empty);
                var haloCustomerTypeId = DynamicValue("HaloCustomerTypeId", 0);
                var haloCustomerTypeName = DynamicValue("HaloCustomerTypeName", string.Empty);
                var haloAccountManagerEmail = DynamicValue<string?>("HaloAccountManagerEmail", null) ?? Environment.GetEnvironmentVariable("HALO_ACCOUNT_MANAGER_EMAIL");
                var haloAccountManagerField = DynamicValue("HaloAccountManagerField", "CFassignedtam");
                var haloNCentralIntegrationId = DynamicValue("HaloNCentralIntegrationId", 0);
                if (haloNCentralIntegrationId <= 0 && int.TryParse(Environment.GetEnvironmentVariable("HALO_NCENTRAL_INTEGRATION_ID"), out var envIntegrationId)) haloNCentralIntegrationId = envIntegrationId;
                var haloBaseUrl = Require(DynamicValue<string?>("HaloBaseUrl", null), "HALO_BASE_URL", "HaloBaseUrl");
                var haloClientId = Require(DynamicValue<string?>("HaloClientId", null), "HALO_CLIENT_ID", "HaloClientId");
                var haloClientSecret = Require(DynamicValue<string?>("HaloClientSecret", null), "HALO_CLIENT_SECRET", "HaloClientSecret");
                var options = new HaloOptions
                {
                    BaseUrl = haloBaseUrl,
                    AccessToken = GetHaloAccessToken(haloBaseUrl, haloClientId, haloClientSecret, haloScope),
                    TopLevelId = haloTopLevelId,
                    DefaultColour = haloDefaultColour,
                    NetSuiteCustomerIdField = haloNetSuiteCustomerIdField,
                    NetSuiteCustomerIdFieldId = haloNetSuiteCustomerIdFieldId,
                    NetSuiteCustomerNameField = haloNetSuiteCustomerNameField,
                    CustomerRelationshipId = haloCustomerRelationshipId,
                    CustomerRelationshipName = haloCustomerRelationshipName,
                    CustomerTypeId = haloCustomerTypeId,
                    CustomerTypeName = haloCustomerTypeName,
                    AccountManagerEmail = haloAccountManagerEmail,
                    AccountManagerField = haloAccountManagerField,
                    NCentralIntegrationId = haloNCentralIntegrationId
                };
                var adapter = new HaloEntityAdapter(options);
                ConnectionRegistry.Set(adapter);
                WriteObject(new EntitySyncConnection { Vendor = adapter.Vendor, Adapter = adapter });
                return;
            }

            if (Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            {
                var nsOptions = new NetSuiteOptions
                {
                    AccountId = ValidateNetSuiteAccountId(Require(DynamicValue<string?>("NetSuiteAccountId", null), "NETSUITE_ACCOUNT_ID", "NetSuiteAccountId")),
                    ConsumerKey = Require(DynamicValue<string?>("NetSuiteConsumerKey", null), "NETSUITE_CONSUMER_KEY", "NetSuiteConsumerKey"),
                    ConsumerSecret = Require(DynamicValue<string?>("NetSuiteConsumerSecret", null), "NETSUITE_CONSUMER_SECRET", "NetSuiteConsumerSecret"),
                    TokenId = Require(DynamicValue<string?>("NetSuiteTokenId", null), "NETSUITE_TOKEN_ID", "NetSuiteTokenId"),
                    TokenSecret = Require(DynamicValue<string?>("NetSuiteTokenSecret", null), "NETSUITE_TOKEN_SECRET", "NetSuiteTokenSecret")
                };
                var nsAdapter = new NetSuiteEntityAdapter(nsOptions);
                ConnectionRegistry.Set(nsAdapter);
                WriteObject(new EntitySyncConnection { Vendor = nsAdapter.Vendor, Adapter = nsAdapter });
                return;
            }

            var ncOptions = new NCentralOptions
            {
                BaseUrl = ValidateAbsoluteHttpsUrl(Require(DynamicValue<string?>("NCentralBaseUrl", null), "NCENTRAL_BASE_URL", "NCentralBaseUrl"), "NCentralBaseUrl"),
                UserApiToken = Require(DynamicValue<string?>("NCentralUserApiToken", null), "NCENTRAL_USER_API_TOKEN", "NCentralUserApiToken"),
                ServiceOrgId = Require(DynamicValue<string?>("NCentralServiceOrgId", null), "NCENTRAL_SERVICE_ORG_ID", "NCentralServiceOrgId"),
                SoapUsername = DynamicValue<string?>("NCentralSoapUsername", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_SOAP_USERNAME") ?? string.Empty,
                SoapPassword = DynamicValue<string?>("NCentralSoapPassword", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_SOAP_PASSWORD") ?? string.Empty,
                SoapEndpointPath = DynamicValue<string?>("NCentralSoapEndpointPath", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_SOAP_ENDPOINT_PATH") ?? "dms2/services2/ServerEI2",
                SoapNamespace = DynamicValue<string?>("NCentralSoapNamespace", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_SOAP_NAMESPACE") ?? "http://ei2.nobj.nable.com/",
                HaloPsaIdPropertyLabel = DynamicValue<string?>("NCentralHaloPsaIdPropertyLabel", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_HALOPSA_ID_PROPERTY_LABEL") ?? "HaloPSA Client ID",
                NetSuiteIdPropertyLabel = DynamicValue<string?>("NCentralNetSuiteIdPropertyLabel", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_NETSUITE_ID_PROPERTY_LABEL") ?? "NetSuite Customer ID",
                NetSuiteNamePropertyLabel = DynamicValue<string?>("NCentralNetSuiteNamePropertyLabel", null) ?? Environment.GetEnvironmentVariable("NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL") ?? "NetSuite Customer Name"
            };
            var ncAdapter = new NCentralEntityAdapter(ncOptions);
            ConnectionRegistry.Set(ncAdapter);
            WriteObject(new EntitySyncConnection { Vendor = ncAdapter.Vendor, Adapter = ncAdapter });
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "ConnectEntitySyncVendorFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }

    private void AddDynamicParameter<T>(string name, T? defaultValue = default)
    {
        if (dynamicParameters == null) return;
        var attributes = new Collection<Attribute> { new ParameterAttribute() };
        var parameter = new RuntimeDefinedParameter(name, typeof(T), attributes) { Value = defaultValue };
        dynamicParameters.Add(name, parameter);
    }

    private T DynamicValue<T>(string name, T defaultValue)
    {
        if (dynamicParameters != null && dynamicParameters.TryGetValue(name, out var parameter) && parameter.Value is T value)
        {
            return value;
        }

        return defaultValue;
    }

    private string Require(string? parameterValue, string environmentVariable, string parameterName)
    {
        var value = parameterValue;
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{parameterName} is required. Pass -{parameterName} or set {environmentVariable}.");
        return value;
    }

    private static string ValidateNetSuiteAccountId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("NetSuiteAccountId is required.");
        if (value.Contains("/", StringComparison.Ordinal) || value.Contains(":", StringComparison.Ordinal)) throw new InvalidOperationException("NetSuiteAccountId must be the account ID only, not a URL. Example: 1234567 or 1234567_SB1.");
        return value.Trim();
    }

    private static string ValidateAbsoluteHttpsUrl(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException($"{parameterName} must be an absolute HTTPS URL.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{parameterName} must use HTTPS.");
        return value.TrimEnd('/');
    }

    private static string GetHaloAccessToken(string baseUrl, string clientId, string clientSecret, string scope)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(baseUrl)) };
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        });

        using var response = httpClient.PostAsync("auth/token", content).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return GetHaloAccessToken(baseUrl, clientId, clientSecret, scope, "token");
        }

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HaloPSA token request failed with HTTP {(int)response.StatusCode}: {body}");
        }

        return ReadHaloAccessToken(body);
    }

    private static string GetHaloAccessToken(string baseUrl, string clientId, string clientSecret, string scope, string tokenPath)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(baseUrl)) };
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        });

        using var response = httpClient.PostAsync(tokenPath, content).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HaloPSA token request failed with HTTP {(int)response.StatusCode}: {body}");
        }

        return ReadHaloAccessToken(body);
    }

    private static string ReadHaloAccessToken(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
        {
            throw new InvalidOperationException("HaloPSA token response did not include access_token.");
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("HaloPSA token response included an empty access_token.");
        return token;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}
