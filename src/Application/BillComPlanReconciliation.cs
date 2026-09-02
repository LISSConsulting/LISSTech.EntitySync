using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public static class BillComPlanReconciliation
{
    public static bool IsAuthoritativeRoute(string sourceVendor, string sourceEntityType, string targetVendor, string targetEntityType) =>
        sourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
        && sourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
        && EntitySyncVendors.IsBillCom(targetVendor)
        && targetEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase);

    public static void AddApprovedTargetOperations(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsAuthoritativeRoute(plan.SourceVendor, plan.SourceEntityType, plan.TargetVendor, plan.TargetEntityType)) return;

        var retainedTargetIds = plan.Items
            .Where(item => item.Target is not null)
            .Select(item => item.Target!.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Items.Where(item => item.Target is not null))
        {
            if (item.Source.Name.Equals(item.Target!.Name, StringComparison.Ordinal)) continue;
            item.Reasons.Add(item.Target.IsActive == false
                ? $"The linked BILL.com value '{item.Target.Name}' (ID {item.Target.Id}) is deleted. Apply will add active value '{item.Source.Name}' and write its new BILL.com ID to HaloPSA."
                : $"BILL.com does not support renaming a custom-field value. Apply will add '{item.Source.Name}', write its new BILL.com ID to HaloPSA, then irreversibly delete '{item.Target.Name}' (ID {item.Target.Id}).");
        }

        foreach (var target in plan.TargetCandidates.Where(target => target.IsActive != false && !retainedTargetIds.Contains(target.Id)))
        {
            plan.Items.Add(new EntitySyncPlanItem
            {
                Action = "Delete",
                Source = new ExternalEntity
                {
                    Vendor = plan.SourceVendor,
                    EntityType = plan.SourceEntityType
                },
                Target = target,
                Score = 100,
                MatchType = "TargetOnly",
                Reasons =
                {
                    $"BILL.com value '{target.Name}' (ID {target.Id}) is absent from the complete HaloPSA client list and will be irreversibly deleted."
                }
            });
        }
    }

    public static void EnsureReadyToApply(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsAuthoritativeRoute(plan.SourceVendor, plan.SourceEntityType, plan.TargetVendor, plan.TargetEntityType)) return;

        var unresolved = plan.Items.Count(item => item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase));
        if (unresolved > 0)
            throw new InvalidOperationException($"BILL.com exact-list reconciliation cannot apply while {unresolved} source item(s) require review. Resolve every source item before approval or apply.");

        if (plan.Items.Any(item => item.Action.Equals("Delete", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.Target?.Id)))
            throw new InvalidOperationException("BILL.com exact-list reconciliation contains a delete action without a target value ID.");
    }

    public static bool IsReplacement(EntitySyncPlan plan, EntitySyncPlanItem item, EntityWriteResult write) =>
        IsAuthoritativeRoute(plan.SourceVendor, plan.SourceEntityType, plan.TargetVendor, plan.TargetEntityType)
        && item.Target is not null
        && item.Target.IsActive != false
        && !string.IsNullOrWhiteSpace(write.Id)
        && !write.Id.Equals(item.Target.Id, StringComparison.OrdinalIgnoreCase);
}
