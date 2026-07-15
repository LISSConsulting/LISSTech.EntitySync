namespace LISSTech.EntitySync.Core;

public static class EntitySyncVendors
{
    public const string AgentController = "AgentController";

    public static string Normalize(string vendor)
    {
        return IsAgentController(vendor) ? AgentController : vendor;
    }

    public static bool IsAgentController(string? vendor)
    {
        return vendor != null
            && (vendor.Equals(AgentController, StringComparison.OrdinalIgnoreCase)
                || vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase));
    }
}
