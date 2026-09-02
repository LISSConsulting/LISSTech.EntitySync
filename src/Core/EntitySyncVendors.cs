namespace LISSTech.EntitySync.Core;

public static class EntitySyncVendors
{
    public const string AgentController = "AgentController";
    public const string BillCom = "Bill.com";
    public const string SophosCentral = "Sophos Central";

    public static string Normalize(string vendor)
    {
        if (IsBillCom(vendor)) return BillCom;
        if (IsSophosCentral(vendor)) return SophosCentral;
        return IsAgentController(vendor) ? AgentController : vendor;
    }

    public static bool IsBillCom(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(BillCom, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BillCom", StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BILL", StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BillSpend", StringComparison.OrdinalIgnoreCase));
    }
    public static bool IsSophosCentral(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(SophosCentral, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("SophosCentral", StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("Sophos", StringComparison.OrdinalIgnoreCase));
    }


    public static bool IsAgentController(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(AgentController, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase));
    }
}
