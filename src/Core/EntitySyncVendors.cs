namespace LISSTech.EntitySync.Core;

public static class EntitySyncVendors
{
    public const string AgentController = "AgentController";
    public const string BillCom = "Bill.com";
    public const string OrchestraMSP = "OrchestraMSP";

    public static string Normalize(string vendor)
    {
        if (IsBillCom(vendor)) return BillCom;
        if (IsAgentController(vendor)) return AgentController;
        return IsOrchestraMSP(vendor) ? OrchestraMSP : vendor;
    }

    public static bool IsBillCom(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(BillCom, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BillCom", StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BILL", StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("BillSpend", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAgentController(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(AgentController, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOrchestraMSP(string? vendor) =>
        vendor != null
        && vendor.Equals(OrchestraMSP, StringComparison.OrdinalIgnoreCase);
}
