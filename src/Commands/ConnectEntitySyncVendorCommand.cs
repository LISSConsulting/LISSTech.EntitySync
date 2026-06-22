using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommunications.Connect, "EntitySyncVendor", DefaultParameterSetName = "HaloPSA")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class ConnectEntitySyncVendorCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, ParameterSetName = "HaloPSA")]
    [Parameter(Mandatory = true, ParameterSetName = "NetSuite")]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string Vendor { get; set; } = string.Empty;

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    public object? GetDynamicParameters()
    {
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
        }
        else if (Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            AddDynamicParameter<string>("NetSuiteRestletUrl");
            AddDynamicParameter<string>("NetSuiteAccountId");
            AddDynamicParameter<string>("NetSuiteConsumerKey");
            AddDynamicParameter<string>("NetSuiteConsumerSecret");
            AddDynamicParameter<string>("NetSuiteTokenId");
            AddDynamicParameter<string>("NetSuiteTokenSecret");
        }

        return dynamicParameters;
    }

    protected override void EndProcessing()
    {
        try
        {
            if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                var haloScope = DynamicValue("HaloScope", "all");
                var haloTopLevelId = DynamicValue("HaloTopLevelId", 1);
                var haloDefaultColour = DynamicValue("HaloDefaultColour", "#E83C4A");
                var haloNetSuiteCustomerIdField = DynamicValue("HaloNetSuiteCustomerIdField", "CFNetSuiteCustomerID");
                var haloBaseUrl = Require(DynamicValue<string?>("HaloBaseUrl", null), "HALO_BASE_URL", "HaloBaseUrl");
                var haloClientId = Require(DynamicValue<string?>("HaloClientId", null), "HALO_CLIENT_ID", "HaloClientId");
                var haloClientSecret = Require(DynamicValue<string?>("HaloClientSecret", null), "HALO_CLIENT_SECRET", "HaloClientSecret");
                var options = new HaloOptions
                {
                    BaseUrl = haloBaseUrl,
                    AccessToken = GetHaloAccessToken(haloBaseUrl, haloClientId, haloClientSecret, haloScope),
                    TopLevelId = haloTopLevelId,
                    DefaultColour = haloDefaultColour,
                    NetSuiteCustomerIdField = haloNetSuiteCustomerIdField
                };
                var adapter = new HaloEntityAdapter(options);
                ConnectionRegistry.Set(adapter);
                WriteObject(new EntitySyncConnection { Vendor = adapter.Vendor, Adapter = adapter });
                return;
            }

            var nsOptions = new NetSuiteOptions
            {
                RestletUrl = ValidateNetSuiteRestletUrl(Require(DynamicValue<string?>("NetSuiteRestletUrl", null), "NETSUITE_RESTLET_URL", "NetSuiteRestletUrl")),
                AccountId = Require(DynamicValue<string?>("NetSuiteAccountId", null), "NETSUITE_ACCOUNT_ID", "NetSuiteAccountId"),
                ConsumerKey = Require(DynamicValue<string?>("NetSuiteConsumerKey", null), "NETSUITE_CONSUMER_KEY", "NetSuiteConsumerKey"),
                ConsumerSecret = Require(DynamicValue<string?>("NetSuiteConsumerSecret", null), "NETSUITE_CONSUMER_SECRET", "NetSuiteConsumerSecret"),
                TokenId = Require(DynamicValue<string?>("NetSuiteTokenId", null), "NETSUITE_TOKEN_ID", "NetSuiteTokenId"),
                TokenSecret = Require(DynamicValue<string?>("NetSuiteTokenSecret", null), "NETSUITE_TOKEN_SECRET", "NetSuiteTokenSecret")
            };
            var nsAdapter = new NetSuiteEntityAdapter(nsOptions);
            ConnectionRegistry.Set(nsAdapter);
            WriteObject(new EntitySyncConnection { Vendor = nsAdapter.Vendor, Adapter = nsAdapter });
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

    private static string ValidateNetSuiteRestletUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException("NetSuiteRestletUrl must be an absolute HTTPS RESTlet URL.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("NetSuiteRestletUrl must use HTTPS.");
        if (!uri.AbsolutePath.EndsWith("/app/site/hosting/restlet.nl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NetSuiteRestletUrl must be the RESTlet external URL, not the SuiteTalk account host root. Expected format: https://<account>.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=<script>&deploy=<deploy>.");
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("script", out var script) || string.IsNullOrWhiteSpace(script) || !query.TryGetValue("deploy", out var deploy) || string.IsNullOrWhiteSpace(deploy))
        {
            throw new InvalidOperationException("NetSuiteRestletUrl must include script and deploy query parameters, for example: https://<account>.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=<script>&deploy=<deploy>.");
        }

        return value;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[Uri.UnescapeDataString(pair[0])] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }

        return values;
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
