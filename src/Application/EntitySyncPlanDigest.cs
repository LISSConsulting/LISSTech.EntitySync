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

    public static EntitySyncSha256 Compute(EntitySyncDurablePlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return EntitySyncCanonicalDigest.Compute(new
        {
            item.TenantId,
            item.PlanId,
            item.ItemId,
            item.ItemOrdinal,
            item.SourceVendor,
            item.SourceConnectionId,
            item.SourceEntityType,
            item.SourceEntityKey,
            item.SourceEntityId,
            item.TargetVendor,
            item.TargetConnectionId,
            item.TargetEntityType,
            item.TargetEntityId,
            item.Action,
            MatchEvidence = new
            {
                item.MatchEvidence.Score,
                item.MatchEvidence.MatchType,
                Reasons = item.MatchEvidence.Reasons.ToArray()
            },
            RedactedBefore = item.RedactedBefore.Json,
            RedactedDesired = item.RedactedDesired.Json,
            BeforePayloadSha256 = item.BeforePayloadSha256?.Value,
            DesiredPayloadSha256 = item.DesiredPayloadSha256.Value,
            ResolvedTargetParent = item.ResolvedTargetParent is null
                ? null
                : new
                {
                    item.ResolvedTargetParent.ClientId,
                    item.ResolvedTargetParent.SiteId,
                    item.ResolvedTargetParent.ParentEntityType,
                    item.ResolvedTargetParent.SourcePlatformInstanceId,
                    item.ResolvedTargetParent.MatchedLinkExternalId,
                    item.ResolvedTargetParent.MatchedLinkStatus,
                    item.ResolvedTargetParent.MatchedLinkToken,
                    item.ResolvedTargetParent.ObservedOwnerVersion
                },
            FieldChanges = item.FieldDiffs.Select(change => new
            {
                change.Field,
                Before = change.Before.Json,
                Desired = change.Desired.Json,
                BeforeSha256 = change.BeforeSha256.Value,
                DesiredSha256 = change.DesiredSha256.Value,
                change.Sensitive
            }).ToArray()
        });
    }
}
