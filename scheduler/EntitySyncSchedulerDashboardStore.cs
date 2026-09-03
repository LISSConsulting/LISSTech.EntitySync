namespace LISSTech.EntitySync.Scheduler;

public sealed class EntitySyncSchedulerDashboardStore(TimeProvider timeProvider)
{
    private const int MaximumRuns = 24;
    private const int MaximumPlans = 40;
    private const int MaximumEvents = 200;
    private const int MaximumMessageLength = 256;
    private readonly object gate = new();
    private readonly List<EntitySyncSchedulerStatusSnapshot> runs = [];
    private readonly List<EntitySyncSchedulerPlanSummary> plans = [];
    private readonly List<EntitySyncSchedulerEvent> events = [];
    private EntitySyncSchedulerOperation? operation;

    internal void BeginRun(DateTimeOffset startedAt)
    {
        lock (gate)
        {
            operation = new EntitySyncSchedulerOperation("AcquireLock", null, null, startedAt);
            AddEventCore("Information", "Scheduler run started.", null);
        }
    }

    internal void SetOperation(string stage, EntitySyncSchedulerRoute? route, string? planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (gate)
        {
            operation = new EntitySyncSchedulerOperation(
                Bound(stage),
                route is null ? null : RouteName(route),
                BoundOptional(planId),
                operation?.StartedAt ?? timeProvider.GetUtcNow());
        }
    }

    internal void RecordPlan(string planId, EntitySyncSchedulerRoute route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentNullException.ThrowIfNull(route);
        lock (gate)
        {
            plans.RemoveAll(plan => plan.PlanId.Equals(planId, StringComparison.Ordinal));
            plans.Insert(0, new EntitySyncSchedulerPlanSummary(
                Bound(planId),
                RouteName(route),
                "Draft",
                timeProvider.GetUtcNow(),
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                0));
            Trim(plans, MaximumPlans);
            AddEventCore("Information", $"Plan created for {RouteName(route)}.", planId);
        }
    }

    internal void RecordPlanValidation(
        string planId,
        int total,
        int changed,
        int unchanged,
        int policySkipped)
    {
        lock (gate)
        {
            UpdatePlan(planId, plan => plan with
            {
                Status = "Validated",
                Total = total,
                Changed = changed,
                Unchanged = unchanged,
                PolicySkipped = policySkipped
            });
            AddEventCore(
                "Information",
                $"Plan validated: {changed} changed, {unchanged} unchanged, {policySkipped} policy-skipped.",
                planId);
        }
    }

    internal void RecordPlanProgress(string planId, int succeeded, int failed, int skipped)
    {
        lock (gate)
        {
            UpdatePlan(planId, plan => plan with
            {
                Status = "Applying",
                Succeeded = succeeded,
                Failed = failed,
                ApplySkipped = skipped
            });
        }
    }

    internal void CompletePlan(string planId, string status, int succeeded, int failed, int skipped)
    {
        lock (gate)
        {
            UpdatePlan(planId, plan => plan with
            {
                Status = Bound(status),
                CompletedAt = timeProvider.GetUtcNow(),
                Succeeded = succeeded,
                Failed = failed,
                ApplySkipped = skipped
            });
            AddEventCore(
                failed == 0 ? "Information" : "Error",
                $"Plan {status.ToLowerInvariant()}: {succeeded} succeeded, {failed} failed, {skipped} skipped.",
                planId);
        }
    }

    internal void FailPlan(string? planId, string message)
    {
        if (string.IsNullOrWhiteSpace(planId)) return;
        lock (gate)
        {
            UpdatePlan(planId, plan => plan with
            {
                Status = "Failed",
                CompletedAt = timeProvider.GetUtcNow()
            });
            AddEventCore("Error", message, planId);
        }
    }

    internal void RecordEvent(string level, string message, string? planId = null)
    {
        lock (gate) AddEventCore(level, message, planId);
    }

    internal void CompleteRun(EntitySyncSchedulerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            runs.Insert(0, snapshot);
            Trim(runs, MaximumRuns);
            operation = null;
            var level = snapshot.State switch
            {
                "Applied" => "Information",
                "SkippedOverlap" => "Warning",
                _ => "Error"
            };
            var message = snapshot.State switch
            {
                "Applied" => "Scheduler run completed successfully.",
                "SkippedOverlap" => "Scheduler run skipped because another replica holds the run lock.",
                _ => $"Scheduler run completed with state {snapshot.State}."
            };
            AddEventCore(level, message, snapshot.PlanId);
        }
    }

    internal EntitySyncSchedulerDashboardSnapshot Snapshot(
        EntitySyncSchedulerStatusSnapshot current,
        EntitySyncSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);
        lock (gate)
        {
            var routes = options.Routes
                .Select((route, index) => new EntitySyncSchedulerRouteSummary(
                    index + 1,
                    route.SourceVendor,
                    route.SourceEntityType,
                    route.TargetVendor,
                    route.TargetEntityType))
                .ToArray();
            return new EntitySyncSchedulerDashboardSnapshot(
                timeProvider.GetUtcNow(),
                current,
                operation,
                routes,
                runs.ToArray(),
                plans.ToArray(),
                events.ToArray());
        }
    }

    private void UpdatePlan(string planId, Func<EntitySyncSchedulerPlanSummary, EntitySyncSchedulerPlanSummary> update)
    {
        var index = plans.FindIndex(plan => plan.PlanId.Equals(planId, StringComparison.Ordinal));
        if (index >= 0) plans[index] = update(plans[index]);
    }

    private void AddEventCore(string level, string message, string? planId)
    {
        events.Insert(0, new EntitySyncSchedulerEvent(
            timeProvider.GetUtcNow(),
            Bound(level),
            Bound(message),
            BoundOptional(planId)));
        Trim(events, MaximumEvents);
    }

    private static string RouteName(EntitySyncSchedulerRoute route) =>
        $"{route.SourceVendor} {route.SourceEntityType} → {route.TargetVendor} {route.TargetEntityType}";

    private static string Bound(string value) =>
        value.Length <= MaximumMessageLength ? value : value[..MaximumMessageLength];

    private static string? BoundOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Bound(value);

    private static void Trim<T>(List<T> values, int maximum)
    {
        if (values.Count > maximum) values.RemoveRange(maximum, values.Count - maximum);
    }
}

internal sealed record EntitySyncSchedulerDashboardSnapshot(
    DateTimeOffset GeneratedAt,
    EntitySyncSchedulerStatusSnapshot Current,
    EntitySyncSchedulerOperation? CurrentOperation,
    IReadOnlyList<EntitySyncSchedulerRouteSummary> Routes,
    IReadOnlyList<EntitySyncSchedulerStatusSnapshot> RecentRuns,
    IReadOnlyList<EntitySyncSchedulerPlanSummary> RecentPlans,
    IReadOnlyList<EntitySyncSchedulerEvent> Events);

internal sealed record EntitySyncSchedulerOperation(
    string Stage,
    string? Route,
    string? PlanId,
    DateTimeOffset StartedAt);

internal sealed record EntitySyncSchedulerRouteSummary(
    int Order,
    string SourceVendor,
    string SourceEntityType,
    string TargetVendor,
    string TargetEntityType);

internal sealed record EntitySyncSchedulerPlanSummary(
    string PlanId,
    string Route,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int Total,
    int Changed,
    int Unchanged,
    int PolicySkipped,
    int Succeeded,
    int Failed,
    int ApplySkipped);

internal sealed record EntitySyncSchedulerEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? PlanId);
