using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public static class ReviewedPlanPolicy
{
    public static void PrepareForReview(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ReviewRequired = true;
        foreach (var item in plan.Items.Where(item => IsExecutable(item.Action))) item.Status = "Planned";
    }

    public static void EnsureApproved(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ReviewRequired) return;
        var unapproved = plan.Items
            .Select((item, index) => new { Item = item, Index = index + 1 })
            .Where(entry => IsExecutable(entry.Item.Action) && !entry.Item.Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Index)
            .ToArray();
        if (unapproved.Length > 0)
            throw new InvalidOperationException($"Reviewed plan contains unapproved executable items: {string.Join(", ", unapproved)}.");
    }

    private static bool IsExecutable(string action) =>
        action.Equals("Create", StringComparison.OrdinalIgnoreCase)
        || action.Equals("Link", StringComparison.OrdinalIgnoreCase)
        || action.Equals("Update", StringComparison.OrdinalIgnoreCase);
}
