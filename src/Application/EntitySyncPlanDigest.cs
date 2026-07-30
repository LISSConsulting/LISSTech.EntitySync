using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
