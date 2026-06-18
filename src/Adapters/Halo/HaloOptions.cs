namespace LISSTech.EntitySync.Adapters.Halo;

public sealed class HaloOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int TopLevelId { get; set; } = 1;
    public string DefaultColour { get; set; } = "#E83C4A";
    public string NetSuiteCustomerIdField { get; set; } = "CFNetSuiteCustomerID";
}
