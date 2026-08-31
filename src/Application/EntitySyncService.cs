using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncService(
    EntitySyncPlanner planner,
    IConnectionRuntimeFactory connections,
    IEntitySyncPlanRepository plans,
    IEntityExclusionRepository exclusions,
    IEntityMapper mapper,
    IEntitySyncChangeStateRepository changeStates,
    TimeProvider? timeProvider = null)
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
        var changeStateRoute = PrepareChangeStateRoute(plan);
        await using var sourceLease = await connections.AcquireAsync(
            tenantId,
            plan.Execution.SourceConnectionId,
            plan.Execution.SourceConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
        await using var targetLease = await connections.AcquireAsync(
            tenantId,
            plan.Execution.TargetConnectionId,
            plan.Execution.TargetConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
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

        var target = targetLease.Adapter;
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
                        write = await target.CreateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (item.Target == null) throw new InvalidOperationException("Target is required for link and update actions.");
                        var request = mapper.MapUpdate(item.Source, item.Target, plan.Execution.MatchOptions);
                        write = await target.UpdateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    if (write.Success)
                    {
                        var checkpointSucceeded = true;
                        if (changeStateRoute is not null)
                        {
                            try
                            {
                                await changeStates.UpsertAsync(new EntitySyncChangeState(
                                    changeStateRoute,
                                    item.Source.Id,
                                    item.Source.Name,
                                    item.Target!.Id,
                                    item.DesiredStateHashVersion!.Value,
                                    item.DesiredStateHash!,
                                    (timeProvider ?? TimeProvider.System).GetUtcNow()), cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                results.Add(new EntitySyncApplyItemResult(
                                    item.Action,
                                    item.Source.Name,
                                    item.Target?.Name,
                                    false,
                                    false,
                                    write.Id,
                                    "Target write succeeded, but change-state checkpoint failed."));
                                failed++;
                                checkpointSucceeded = false;
                            }
                        }

                        if (checkpointSucceeded)
                        {
                            results.Add(new EntitySyncApplyItemResult(
                                item.Action,
                                item.Source.Name,
                                item.Target?.Name,
                                true,
                                false,
                                write.Id,
                                write.Message ?? "Target write succeeded."));
                            succeeded++;
                        }
                    }
                    else
                    {
                        results.Add(new EntitySyncApplyItemResult(
                            item.Action,
                            item.Source.Name,
                            item.Target?.Name,
                            false,
                            false,
                            write.Id,
                            "Target write failed."));
                        failed++;
                    }
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
    private static EntitySyncChangeStateRoute? PrepareChangeStateRoute(EntitySyncPlan plan)
    {
        if (plan.Execution.UpdatePolicy != EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly)
            return null;

        var route = EntitySyncChangeStateRoute.Create(
            plan.TenantId,
            plan.Execution.ChangeStateScope ?? string.Empty,
            plan.SourceVendor,
            plan.Execution.SourceConnectionId,
            plan.SourceEntityType,
            plan.TargetVendor,
            plan.Execution.TargetConnectionId,
            plan.TargetEntityType);

        foreach (var item in plan.Items)
        {
            if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)
                || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Changed-only plans may apply update actions only.");
            if (item.Target is null
                || string.IsNullOrWhiteSpace(item.Target.Id)
                || item.DesiredStateHashVersion != EntityWriteRequestDigest.SchemaVersion
                || !IsLowercaseSha256(item.DesiredStateHash))
                throw new InvalidOperationException("Changed-only update checkpoint metadata is missing or invalid.");
        }

        return route;
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

}
