using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncService(
    EntitySyncPlanner planner,
    IEntityConnectionRepository connections,
    IEntitySyncPlanRepository plans,
    IEntityExclusionRepository exclusions,
    IEntityMapper mapper)
{
    public Task<EntitySyncPlan> CreatePlanAsync(CreateEntitySyncPlanRequest request, CancellationToken cancellationToken) => planner.CreateAsync(request, cancellationToken);

    public EntitySyncPlanPage GetPlan(string tenantId, string planId, int page = 1, int pageSize = 25)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
        var plan = plans.Get(tenantId, planId);
        var start = checked((page - 1) * pageSize);
        var items = plan.Items.Skip(start).Take(pageSize)
            .Select((item, offset) => new EntitySyncPlanItemView(
                start + offset,
                item.Action,
                item.MatchType,
                item.Score,
                item.Source.Id,
                item.Source.Name,
                item.Target?.Id,
                item.Target?.Name,
                item.Reasons))
            .ToArray();
        var digest = EntitySyncPlanDigest.Compute(plan);
        plans.RecordInspection(tenantId, planId, digest, start, items.Length);
        return new EntitySyncPlanPage(plan.Id, plan.Status, digest, page, pageSize, plan.Items.Count, items);
    }

    public string ApprovePlan(string tenantId, string planId, string expectedDigest)
    {
        var plan = plans.Get(tenantId, planId);
        var digest = EntitySyncPlanDigest.Compute(plan);
        if (!digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Plan changed after inspection; inspect it again before approval.");
        if (!plans.TryApprove(tenantId, planId, digest))
            throw new InvalidOperationException($"Plan '{planId}' cannot be approved. Inspect every plan item with the current digest and ensure the plan is still a draft.");
        return digest;
    }

    public async Task<EntitySyncApplyResult> ApplyAsync(
        string tenantId,
        string planId,
        bool apply,
        CancellationToken cancellationToken,
        Action<EntitySyncApplyProgress>? reportProgress = null)
    {
        var plan = plans.Get(tenantId, planId);
        using var sourceLease = connections.Acquire(tenantId, plan.SourceVendor, plan.Execution.SourceConnectionId, plan.Execution.SourceConnectionGeneration);
        using var targetLease = connections.Acquire(tenantId, plan.TargetVendor, plan.Execution.TargetConnectionId, plan.Execution.TargetConnectionGeneration);
        if (plan.Items.Any(item => item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)))
        {
            var route = EntityExclusionRoute.Create(
                tenantId,
                plan.SourceVendor,
                plan.Execution.SourceConnectionId,
                plan.SourceEntityType,
                plan.TargetVendor,
                plan.Execution.TargetConnectionId,
                plan.TargetEntityType);
            IReadOnlyList<EntityExclusion> activeExclusions;
            try
            {
                activeExclusions = await exclusions.ListActiveAsync(route, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new EntityExclusionUnavailableException(
                    "Permanent exclusions could not be obtained; create actions are blocked.",
                    ex);
            }
            var excludedSourceIds = activeExclusions
                .Select(exclusion => exclusion.SourceEntityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var staleCreates = plan.Items
                .Where(item => item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
                    && excludedSourceIds.Contains(item.Source.Id))
                .Select(item => item.Source.Id)
                .ToArray();
            if (staleCreates.Length > 0)
                throw new InvalidOperationException("Permanent exclusions changed after planning; create and inspect a new plan.");
        }
        if (apply)
        {
            if (!plan.Status.Equals(EntitySyncPlanStatuses.Approved, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Plan must be approved before apply.");
            var digest = EntitySyncPlanDigest.Compute(plan);
            if (!digest.Equals(plan.ApprovedDigest, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Approved plan digest no longer matches the plan.");
            if (!plans.TryTransition(tenantId, planId, EntitySyncPlanStatuses.Approved, EntitySyncPlanStatuses.Applying)) throw new InvalidOperationException("Plan is already being applied or has been consumed.");
        }

        var target = targetLease.Connection;
        var results = new List<EntitySyncApplyItemResult>();
        var completed = false;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        void ReportProgress()
        {
            reportProgress?.Invoke(new EntitySyncApplyProgress(
                plan.Items.Count,
                results.Count,
                succeeded,
                failed,
                skipped,
                results[^1]));
        }
        try
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase) || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new EntitySyncApplyItemResult(item.Action, item.Source.Name, item.Target?.Name, true, true, null, "Skipped: requires review or no action."));
                    skipped++;
                    ReportProgress();
                    continue;
                }
                if (!apply)
                {
                    results.Add(new EntitySyncApplyItemResult(item.Action, item.Source.Name, item.Target?.Name, true, false, null, "Dry-run: no write performed."));
                    succeeded++;
                    ReportProgress();
                    continue;
                }

                try
                {
                    EntityWriteResult write;
                    if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                    {
                        var request = mapper.MapCreate(item.Source, plan.TargetVendor, plan.TargetEntityType, plan.Execution.MatchOptions);
                        write = await target.Adapter.CreateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (item.Target == null) throw new InvalidOperationException("Target is required for link and update actions.");
                        var request = mapper.MapUpdate(item.Source, item.Target, plan.Execution.MatchOptions);
                        write = await target.Adapter.UpdateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    results.Add(new EntitySyncApplyItemResult(item.Action, item.Source.Name, item.Target?.Name, write.Success, false, write.Id, write.Success ? write.Message ?? "Target write succeeded." : "Target write failed."));
                    if (write.Success)
                        succeeded++;
                    else
                        failed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    results.Add(new EntitySyncApplyItemResult(item.Action, item.Source.Name, item.Target?.Name, false, false, null, "Target write failed."));
                    failed++;
                }
                ReportProgress();
            }
            completed = true;
        }
        finally
        {
            if (apply)
            {
                var applyFailed = !completed || failed > 0;
                plans.TryTransition(tenantId, planId, EntitySyncPlanStatuses.Applying, applyFailed ? EntitySyncPlanStatuses.Failed : EntitySyncPlanStatuses.Applied);
            }
        }

        return new EntitySyncApplyResult(plan.Id, apply, failed == 0, succeeded, failed, skipped, results);
    }
}
