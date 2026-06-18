using System.Management.Automation;
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
    public string? HaloAccessToken { get; set; }

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
                var options = new HaloOptions
                {
                    BaseUrl = Require(HaloBaseUrl, "HALO_BASE_URL", "HaloBaseUrl"),
                    AccessToken = Require(HaloAccessToken, "HALO_ACCESS_TOKEN", "HaloAccessToken"),
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
}
