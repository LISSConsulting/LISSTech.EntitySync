using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommunications.Connect, "EntitySyncVendor", DefaultParameterSetName = "HaloPSA")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class ConnectEntitySyncVendorCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, ParameterSetName = "HaloPSA")]
    [Parameter(Mandatory = true, ParameterSetName = "NetSuite")]
    [Parameter(Mandatory = true, ParameterSetName = "NCentral")]
    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerToken")]
    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerSecureToken")]
    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerSession")]
    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerDeviceAssetOpsProfile")]
    [ArgumentCompleter(typeof(EntitySyncVendorCompleter))]
    public string Vendor { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Profile")]
    [Parameter(ParameterSetName = "HaloPSA")]
    [Parameter(ParameterSetName = "NetSuite")]
    [Parameter(ParameterSetName = "NCentral")]
    [Parameter(ParameterSetName = "AgentControllerToken")]
    [Parameter(ParameterSetName = "AgentControllerSecureToken")]
    [Parameter(ParameterSetName = "AgentControllerSession")]
    [Parameter(ParameterSetName = "AgentControllerDeviceAssetOpsProfile")]
    public string? Profile { get; set; }

    [Parameter(ParameterSetName = "HaloPSA")]
    [Parameter(ParameterSetName = "NetSuite")]
    [Parameter(ParameterSetName = "NCentral")]
    [Parameter(ParameterSetName = "AgentControllerToken")]
    [Parameter(ParameterSetName = "AgentControllerSecureToken")]
    [Parameter(ParameterSetName = "AgentControllerSession")]
    [Parameter(ParameterSetName = "AgentControllerDeviceAssetOpsProfile")]
    public SwitchParameter SaveProfile { get; set; }

    [Parameter(ParameterSetName = "HaloPSA")]
    [Parameter(ParameterSetName = "NetSuite")]
    [Parameter(ParameterSetName = "NCentral")]
    [Parameter(ParameterSetName = "AgentControllerToken")]
    [Parameter(ParameterSetName = "AgentControllerSecureToken")]
    [Parameter(ParameterSetName = "AgentControllerSession")]
    [Parameter(ParameterSetName = "AgentControllerDeviceAssetOpsProfile")]
    public SwitchParameter DefaultProfile { get; set; }

    [Parameter(ParameterSetName = "AgentControllerSecureToken")]
    public SecureString? SecureToken { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerSession")]
    public PSObject? Session { get; set; }

    [Parameter(ParameterSetName = "AgentControllerSession")]
    [Parameter(Mandatory = true, ParameterSetName = "AgentControllerDeviceAssetOpsProfile")]
    public string? DeviceAssetOpsProfile { get; set; }

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    /// <summary>
    /// LTAC values are normalized to the cmdlet-facing AgentController vendor name.
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) => EntitySyncVendors.Normalize(vendor);

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
        else if (EntitySyncVendors.IsAgentController(Vendor))
        {
            AddDynamicParameter<string>("Url", parameterSetNames: new[] { "AgentControllerToken", "AgentControllerSecureToken" });
            AddDynamicParameter<string>("Token", parameterSetNames: new[] { "AgentControllerToken" });
        }

        return dynamicParameters;
    }

    protected override void EndProcessing()
    {
        try
        {
            if (ParameterSetName.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var output in ConnectEntitySyncProfileCommand.ConnectProfile(Profile)) WriteObject(output);
                return;
            }

            Vendor = NormalizeVendorAlias(Vendor);

            if (EntitySyncVendors.IsAgentController(Vendor))
            {
                if (ParameterSetName.Equals("AgentControllerSession", StringComparison.OrdinalIgnoreCase))
                {
                    var session = Session ?? throw new InvalidOperationException("Session is required.");
                    ConnectAgentControllerFromSession(session, DeviceAssetOpsProfile);
                    return;
                }

                if (ParameterSetName.Equals("AgentControllerDeviceAssetOpsProfile", StringComparison.OrdinalIgnoreCase))
                {
                    var deviceAssetOpsProfileName = RequireProfileReference(DeviceAssetOpsProfile);
                    ConnectAgentControllerFromSession(GetDeviceAssetOpsSession(deviceAssetOpsProfileName), deviceAssetOpsProfileName);
                    return;
                }

                if (SaveProfile)
                {
                    throw new InvalidOperationException("AgentController profiles store a DeviceAssetOps profile reference, not short-lived bearer tokens. Use -DeviceAssetOpsProfile with -SaveProfile.");
                }

                var stringTokenExplicit = DynamicValue<string?>("Token", null);
                var hasSecure = SecureToken != null;
                var hasStringExplicit = !string.IsNullOrWhiteSpace(stringTokenExplicit);

                if (hasSecure && hasStringExplicit)
                {
                    throw new InvalidOperationException(
                        "-Token and -SecureToken are mutually exclusive. Pass exactly one.");
                }

                var bearerToken = hasSecure
                    ? UnwrapSecureString(SecureToken, "SecureToken")
                    : Require(stringTokenExplicit, "LTAC_BEARER_TOKEN", "Token");

                var ltacOptions = new LTACOptions
                {
                    BaseUrl = ValidateAbsoluteHttpsUrl(Require(DynamicValue<string?>("Url", null), "LTAC_BASE_URL", "Url"), "Url"),
                    BearerToken = bearerToken
                };
                var ltacAdapter = new LTACEntityAdapter(ltacOptions);
                ConnectionRegistry.Set(ltacAdapter);
                WriteObject(new EntitySyncConnection { Vendor = ltacAdapter.Vendor, Adapter = ltacAdapter });
                return;
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
                SaveProfileIfRequested(adapter.Vendor, new Dictionary<string, string?>
                {
                    ["HaloBaseUrl"] = haloBaseUrl,
                    ["HaloClientId"] = haloClientId,
                    ["HaloClientSecret"] = haloClientSecret,
                    ["HaloScope"] = haloScope,
                    ["HaloTopLevelId"] = haloTopLevelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["HaloDefaultColour"] = haloDefaultColour,
                    ["HaloNetSuiteCustomerIdField"] = haloNetSuiteCustomerIdField,
                    ["HaloNetSuiteCustomerIdFieldId"] = haloNetSuiteCustomerIdFieldId,
                    ["HaloNetSuiteCustomerNameField"] = haloNetSuiteCustomerNameField,
                    ["HaloCustomerRelationshipId"] = haloCustomerRelationshipId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["HaloCustomerRelationshipName"] = haloCustomerRelationshipName,
                    ["HaloCustomerTypeId"] = haloCustomerTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["HaloCustomerTypeName"] = haloCustomerTypeName,
                    ["HaloAccountManagerEmail"] = haloAccountManagerEmail,
                    ["HaloAccountManagerField"] = haloAccountManagerField,
                    ["HaloNCentralIntegrationId"] = haloNCentralIntegrationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
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
                SaveProfileIfRequested(nsAdapter.Vendor, new Dictionary<string, string?>
                {
                    ["NetSuiteAccountId"] = nsOptions.AccountId,
                    ["NetSuiteConsumerKey"] = nsOptions.ConsumerKey,
                    ["NetSuiteConsumerSecret"] = nsOptions.ConsumerSecret,
                    ["NetSuiteTokenId"] = nsOptions.TokenId,
                    ["NetSuiteTokenSecret"] = nsOptions.TokenSecret
                });
                WriteObject(new EntitySyncConnection { Vendor = nsAdapter.Vendor, Adapter = nsAdapter });
                return;
            }

            if (!Vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Vendor '{Vendor}' is not supported.");

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
            SaveProfileIfRequested(ncAdapter.Vendor, new Dictionary<string, string?>
            {
                ["NCentralBaseUrl"] = ncOptions.BaseUrl,
                ["NCentralUserApiToken"] = ncOptions.UserApiToken,
                ["NCentralServiceOrgId"] = ncOptions.ServiceOrgId,
                ["NCentralSoapUsername"] = ncOptions.SoapUsername,
                ["NCentralSoapPassword"] = ncOptions.SoapPassword,
                ["NCentralSoapEndpointPath"] = ncOptions.SoapEndpointPath,
                ["NCentralSoapNamespace"] = ncOptions.SoapNamespace,
                ["NCentralHaloPsaIdPropertyLabel"] = ncOptions.HaloPsaIdPropertyLabel,
                ["NCentralNetSuiteIdPropertyLabel"] = ncOptions.NetSuiteIdPropertyLabel,
                ["NCentralNetSuiteNamePropertyLabel"] = ncOptions.NetSuiteNamePropertyLabel
            });
            WriteObject(new EntitySyncConnection { Vendor = ncAdapter.Vendor, Adapter = ncAdapter });
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "ConnectEntitySyncVendorFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }

    private void AddDynamicParameter<T>(string name, T? defaultValue = default, IReadOnlyList<string>? parameterSetNames = null)
    {
        if (dynamicParameters == null) return;
        var attributes = new Collection<Attribute>();
        if (parameterSetNames == null || parameterSetNames.Count == 0)
        {
            attributes.Add(new ParameterAttribute());
        }
        else
        {
            foreach (var parameterSetName in parameterSetNames)
            {
                attributes.Add(new ParameterAttribute { ParameterSetName = parameterSetName });
            }
        }
        var parameter = new RuntimeDefinedParameter(name, typeof(T), attributes) { Value = defaultValue };
        dynamicParameters.Add(name, parameter);
    }

    private static string UnwrapSecureString(SecureString? secureString, string parameterName)
    {
        if (secureString == null || secureString.Length == 0)
        {
            throw new InvalidOperationException($"{parameterName} is required and must not be empty.");
        }

        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secureString);
        try
        {
            var value = Marshal.PtrToStringUni(ptr);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{parameterName} is required and must not be empty.");
            }
            return value;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
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

    private void SaveProfileIfRequested(string vendor, IReadOnlyDictionary<string, string?> settings)
    {
        if (!SaveProfile) return;
        if (string.IsNullOrWhiteSpace(Profile)) throw new InvalidOperationException("Profile is required when SaveProfile is specified.");
        EntitySyncProfileStore.SaveVendor(Profile, vendor, settings, DefaultProfile);
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

    private static string GetSessionStringProperty(PSObject session, params string[] names)
    {
        foreach (var name in names)
        {
            var property = session.Properties[name];
            if (property?.Value is string value && !string.IsNullOrWhiteSpace(value)) return value;
        }

        throw new InvalidOperationException($"Session must include {string.Join(" or ", names)}.");
    }

    private static SecureString GetSessionSecureToken(PSObject session)
    {
        var token = session.Properties["Token"]?.Value as SecureString;
        if (token == null)
        {
            token = session.Properties["SecureToken"]?.Value as SecureString;
        }

        return token ?? throw new InvalidOperationException("Session must include a SecureString Token.");
    }

    private void ConnectAgentControllerFromSession(PSObject session, string? deviceAssetOpsProfileName)
    {
        if (SaveProfile && string.IsNullOrWhiteSpace(deviceAssetOpsProfileName))
        {
            throw new InvalidOperationException("Saving an AgentController profile from -Session requires -DeviceAssetOpsProfile so future connects can mint a fresh short-lived token.");
        }

        var sessionToken = GetSessionSecureToken(session);
        var sessionOpsBaseUrl = GetSessionStringProperty(session, "OpsBaseUrl", "Uri");
        var sessionOptions = new LTACOptions
        {
            BaseUrl = ValidateAbsoluteHttpsUrl(sessionOpsBaseUrl, "Session.OpsBaseUrl"),
            BearerToken = UnwrapSecureString(sessionToken, "Session.Token")
        };
        var sessionAdapter = new LTACEntityAdapter(sessionOptions);
        ConnectionRegistry.Set(sessionAdapter);
        SaveProfileIfRequested(sessionAdapter.Vendor, new Dictionary<string, string?>
        {
            ["DeviceAssetOpsProfile"] = deviceAssetOpsProfileName
        });
        WriteObject(new EntitySyncConnection { Vendor = sessionAdapter.Vendor, Adapter = sessionAdapter });
    }

    private static string RequireProfileReference(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("DeviceAssetOpsProfile is required.");
        return profileName.Trim();
    }

    private static PSObject GetDeviceAssetOpsSession(string profileName)
    {
        using (var connect = PowerShell.Create(RunspaceMode.CurrentRunspace))
        {
            connect.AddCommand("Connect-DeviceAssetOps").AddParameter("Profile", profileName);
            connect.Invoke();
            if (connect.HadErrors) throw new InvalidOperationException(string.Join(Environment.NewLine, connect.Streams.Error.Select(error => error.ToString())));
        }

        using var token = PowerShell.Create(RunspaceMode.CurrentRunspace);
        token.AddCommand("Get-DeviceAssetOpsAccessToken").AddParameter("AsSession");
        var result = token.Invoke();
        if (token.HadErrors) throw new InvalidOperationException(string.Join(Environment.NewLine, token.Streams.Error.Select(error => error.ToString())));
        return result.Count == 1 ? result[0] : throw new InvalidOperationException($"Expected one DeviceAssetOps session from Get-DeviceAssetOpsAccessToken -AsSession, got {result.Count}.");
    }

    private static string GetHaloAccessToken(string baseUrl, string clientId, string clientSecret, string scope)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(UrlHelpers.EnsureTrailingSlash(baseUrl)) };
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
        using var httpClient = new HttpClient { BaseAddress = new Uri(UrlHelpers.EnsureTrailingSlash(baseUrl)) };
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
}
