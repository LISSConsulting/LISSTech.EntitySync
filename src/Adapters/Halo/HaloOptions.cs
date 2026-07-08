namespace LISSTech.EntitySync.Adapters.Halo;

public sealed class HaloOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int TopLevelId { get; set; } = 1;
    public string DefaultColour { get; set; } = "#E83C4A";
    public string NetSuiteCustomerIdField { get; set; } = "CFNetSuiteCustomerID";
    public string NetSuiteCustomerIdFieldId { get; set; } = string.Empty;
    public string NetSuiteCustomerNameField { get; set; } = "CFNetSuiteCustomerName";
    public int CustomerRelationshipId { get; set; }
    public string CustomerRelationshipName { get; set; } = string.Empty;
    public int CustomerTypeId { get; set; }
    public string CustomerTypeName { get; set; } = string.Empty;
    public string? AccountManagerEmail { get; set; }
    public string AccountManagerField { get; set; } = "CFassignedtam";
    public int NCentralIntegrationId { get; set; }
}
