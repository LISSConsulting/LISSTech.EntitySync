namespace LISSTech.EntitySync.Adapters.NCentral;

public sealed class NCentralOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string UserApiToken { get; set; } = string.Empty;
    public string ServiceOrgId { get; set; } = string.Empty;
    public string SoapUsername { get; set; } = string.Empty;
    public string SoapPassword { get; set; } = string.Empty;
    public string SoapEndpointPath { get; set; } = "dms2/services2/ServerEI2";
    public string SoapNamespace { get; set; } = "http://ei2.nobj.nable.com/";
    public string HaloPsaIdPropertyLabel { get; set; } = "HaloPSA Client ID";
    public string NetSuiteIdPropertyLabel { get; set; } = "NetSuite Customer ID";
    public string NetSuiteNamePropertyLabel { get; set; } = "NetSuite Customer Name";
}
