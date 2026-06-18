namespace LISSTech.EntitySync.Core;

public sealed class MatchOptions
{
    public string SourceExternalIdName { get; set; } = "NetSuiteInternalId";
    public string TargetExternalIdName { get; set; } = "NetSuiteInternalId";
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";
    public int AutoLinkScore { get; set; } = 90;
    public int ReviewScore { get; set; } = 70;
    public bool CreateMissing { get; set; }
}
