using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommunications.Connect, "EntitySyncVendor", DefaultParameterSetName = "HaloPSA")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class ConnectEntitySyncVendorCommand : PSCmdlet
{
    [Parameter(Mandatory = true, ParameterSetName = "HaloPSA")]
    [Parameter(Mandatory = true, ParameterSetName = "NetSuite")]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string Vendor { get; set; } = string.Empty;

    [Parameter(ParameterSetName = "HaloPSA")]
    public string? HaloBaseUrl { get; set; }

    [Parameter(ParameterSetName = "HaloPSA")]
    public string? HaloClientId { get; set; }

    [Parameter(ParameterSetName = "HaloPSA")]
    public string? HaloClientSecret { get; set; }

    [Parameter(ParameterSetName = "HaloPSA")]
    public string HaloScope { get; set; } = "all";

    [Parameter(ParameterSetName = "HaloPSA")]
    public int HaloTopLevelId { get; set; } = 1;

    [Parameter(ParameterSetName = "HaloPSA")]
    public string HaloDefaultColour { get; set; } = "#E83C4A";

    [Parameter(ParameterSetName = "HaloPSA")]
    public string HaloNetSuiteCustomerIdField { get; set; } = "CFNetSuiteCustomerID";

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteRestletUrl { get; set; }

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteAccountId { get; set; }

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteConsumerKey { get; set; }

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteConsumerSecret { get; set; }

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteTokenId { get; set; }

    [Parameter(ParameterSetName = "NetSuite")]
    public string? NetSuiteTokenSecret { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                var haloBaseUrl = Require(HaloBaseUrl, "HALO_BASE_URL", "HaloBaseUrl");
                var haloClientId = Require(HaloClientId, "HALO_CLIENT_ID", "HaloClientId");
                var haloClientSecret = Require(HaloClientSecret, "HALO_CLIENT_SECRET", "HaloClientSecret");
                var options = new HaloOptions
                {
                    BaseUrl = haloBaseUrl,
                    AccessToken = GetHaloAccessToken(haloBaseUrl, haloClientId, haloClientSecret, HaloScope),
                    TopLevelId = HaloTopLevelId,
                    DefaultColour = HaloDefaultColour,
                    NetSuiteCustomerIdField = HaloNetSuiteCustomerIdField
                };
                var adapter = new HaloEntityAdapter(options);
                ConnectionRegistry.Set(adapter);
                WriteObject(new EntitySyncConnection { Vendor = adapter.Vendor, Adapter = adapter });
                return;
            }

            var nsOptions = new NetSuiteOptions
            {
                RestletUrl = Require(NetSuiteRestletUrl, "NETSUITE_RESTLET_URL", "NetSuiteRestletUrl"),
                AccountId = Require(NetSuiteAccountId, "NETSUITE_ACCOUNT_ID", "NetSuiteAccountId"),
                ConsumerKey = Require(NetSuiteConsumerKey, "NETSUITE_CONSUMER_KEY", "NetSuiteConsumerKey"),
                ConsumerSecret = Require(NetSuiteConsumerSecret, "NETSUITE_CONSUMER_SECRET", "NetSuiteConsumerSecret"),
                TokenId = Require(NetSuiteTokenId, "NETSUITE_TOKEN_ID", "NetSuiteTokenId"),
                TokenSecret = Require(NetSuiteTokenSecret, "NETSUITE_TOKEN_SECRET", "NetSuiteTokenSecret")
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

    private string Require(string? parameterValue, string environmentVariable, string parameterName)
    {
        var value = parameterValue;
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{parameterName} is required. Pass -{parameterName} or set {environmentVariable}.");
        return value;
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
