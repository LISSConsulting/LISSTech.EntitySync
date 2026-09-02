using System.Text;

namespace LISSTech.EntitySync.Core;

public static class EntitySyncIntegrationContracts
{
    public const string BillComHaloClientCustomFieldName = "CFBillSpendClientID";
    public const string BillComClientExternalIdName = "BillSpendClientId";
    public const string SophosCentralHaloTenantCustomFieldName = "CFSophosCentralTenantID";
    public const string SophosCentralTenantExternalIdName = "SophosCentralTenantId";


    public static string DecodeBillComValueId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return string.Empty;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rawId));
            var colonIndex = decoded.LastIndexOf(':');
            return colonIndex >= 0 && colonIndex < decoded.Length - 1 ? decoded[(colonIndex + 1)..] : rawId;
        }
        catch (FormatException)
        {
            return rawId;
        }
    }

    public static string SanitizeNCentralName(string value)
    {
        var sanitized = value.Replace("&", " and ", StringComparison.Ordinal);
        return string.Join(" ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
