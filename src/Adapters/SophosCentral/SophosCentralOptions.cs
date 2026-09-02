namespace LISSTech.EntitySync.Adapters.SophosCentral;

public sealed class SophosCentralOptions
{
    internal const string DefaultIdentityUrl = "https://id.sophos.com/api/v2/oauth2/token";
    internal const string DefaultGlobalApiUrl = "https://api.central.sophos.com/";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string? DefaultDataGeography { get; set; }
    public string? DefaultDataRegion { get; set; }
    public string? DefaultBillingType { get; set; }


    internal string IdentityUrl { get; set; } = DefaultIdentityUrl;
    internal string GlobalApiUrl { get; set; } = DefaultGlobalApiUrl;
}
