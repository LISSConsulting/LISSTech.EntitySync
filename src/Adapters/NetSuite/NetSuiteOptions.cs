namespace LISSTech.EntitySync.Adapters.NetSuite;

public sealed class NetSuiteOptions
{
    public string AccountId { get; set; } = string.Empty;
    public string ConsumerKey { get; set; } = string.Empty;
    public string ConsumerSecret { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
    public string TokenSecret { get; set; } = string.Empty;

    // Optional SuiteQL endpoint override. Empty (the default) preserves the standard
    // https://{AccountId}.suitetalk.api.netsuite.com/services/rest/query/v1/suiteql URL the
    // adapter has always composed from AccountId. When non-empty the override is used as the
    // scheme+authority (path is always /services/rest/query/v1/suiteql), which lets test code
    // point the adapter at a local mock without DNS tricks and gives operators a way to target
    // alternate SuiteTalk hosts (for example, proxies mirroring the canonical endpoint).
    public string BaseUrl { get; set; } = string.Empty;
}
