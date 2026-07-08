namespace LISSTech.EntitySync.Core;

public static class EntitySyncLookupTypes
{
    public const string TopLevel = "TopLevel";
    public const string CustomerRelationship = "CustomerRelationship";
    public const string CustomerType = "CustomerType";
    public const string NCentralIntegration = "NCentralIntegration";
    public const string NCentralIntegrationLink = "NCentralIntegrationLink";
    public const string ServiceOrganization = "ServiceOrganization";

    public static IReadOnlyList<string> ForVendor(string vendor)
    {
        if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return new[] { TopLevel, CustomerRelationship, CustomerType, NCentralIntegration, NCentralIntegrationLink };
        if (vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) return new[] { ServiceOrganization };
        return Array.Empty<string>();
    }
}
