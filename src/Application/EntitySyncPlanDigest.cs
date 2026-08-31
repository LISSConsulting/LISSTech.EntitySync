using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public static class EntitySyncPlanDigest
{
    public static string Compute(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = new
        {
            plan.Id,
            plan.TenantId,
            plan.SourceVendor,
            plan.SourceEntityType,
            plan.TargetVendor,
            plan.TargetEntityType,
            plan.CreatedAt,
            plan.ExpiresAt,
            plan.Execution,
            plan.Items
        };
        return EntitySyncCanonicalDigest.Compute(canonical).Value;
    }
}
