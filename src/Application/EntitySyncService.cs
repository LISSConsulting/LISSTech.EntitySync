using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncService(
    EntitySyncPlanner planner,
    IEntityConnectionRepository connections,
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
        BillComPlanReconciliation.EnsureReadyToApply(plan);
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
        if (apply) BillComPlanReconciliation.EnsureReadyToApply(plan);
        var changeStateRoute = PrepareChangeStateRoute(plan);
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

        var source = sourceLease.Connection;
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
            if (apply && EntitySyncVendors.IsAgentController(plan.TargetVendor))
            {
                if (target.Adapter is not IEntityBatchAdapter batchAdapter)
                    throw new InvalidOperationException("AgentController target connection does not support authoritative batch apply.");

                var batchItems = plan.Items
                    .Where(item => !item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)
                        && !item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var batchRequests = batchItems
                    .Select(item => item.Target == null
                        ? mapper.MapCreate(item.Source, plan.TargetVendor, plan.TargetEntityType, plan.Execution.MatchOptions)
                        : mapper.MapUpdate(item.Source, item.Target, plan.Execution.MatchOptions))
                    .ToArray();
                var batchWrite = batchRequests.Length == 0
                    ? new EntityWriteResult
                    {
                        Vendor = plan.TargetVendor,
                        EntityType = plan.TargetEntityType,
                        Action = "None",
                        Success = true,
                        Message = "No approved AgentController customer scopes required synchronization."
                    }
                    : await batchAdapter.ApplyBatchAsync(batchRequests, cancellationToken).ConfigureAwait(false);

                foreach (var item in plan.Items)
                {
                    var skippedItem = item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)
                        || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase);
                    var success = skippedItem || batchWrite.Success;
                    results.Add(new EntitySyncApplyItemResult(
                        item.Action,
                        item.Source.Name,
                        item.Target?.Name,
                        success,
                        skippedItem,
                        item.Target?.Id,
                        skippedItem ? "Skipped: requires review or no action." : batchWrite.Message ?? "AgentController batch sync applied."));
                    if (skippedItem) skipped++;
                    else if (success) succeeded++;
                    else failed++;
                    ReportProgress();
                }

                completed = true;
                return new EntitySyncApplyResult(plan.Id, true, failed == 0, succeeded, failed, skipped, results);
            }

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
                    EntityWriteRequest request;
                    EntityWriteResult write;
                    var deleteAction = item.Action.Equals("Delete", StringComparison.OrdinalIgnoreCase);
                    if (deleteAction)
                    {
                        if (failed > 0)
                        {
                            results.Add(new EntitySyncApplyItemResult(item.Action, item.Target?.Name ?? string.Empty, item.Target?.Name, false, true, item.Target?.Id, "Delete skipped because an earlier exact-list operation failed."));
                            skipped++;
                            ReportProgress();
                            continue;
                        }
                        if (item.Target is null) throw new InvalidOperationException("Target is required for delete actions.");
                        if (target.Adapter is not IEntityDeleteAdapter deleteAdapter) throw new InvalidOperationException($"{plan.TargetVendor} does not support entity deletion.");
                        request = DeleteRequest(plan, item.Target);
                        write = await deleteAdapter.DeleteEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    else if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                    {
                        request = mapper.MapCreate(item.Source, plan.TargetVendor, plan.TargetEntityType, plan.Execution.MatchOptions);
                        write = await target.Adapter.CreateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (item.Target == null) throw new InvalidOperationException("Target is required for link and update actions.");
                        request = mapper.MapUpdate(item.Source, item.Target, plan.Execution.MatchOptions);
                        write = await target.Adapter.UpdateEntityAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    if (write.Success)
                    {
                        EntityWriteResult? sourceWriteback;
                        try
                        {
                            sourceWriteback = deleteAction
                                ? null
                                : await WriteSourceIntegrationAsync(
                                    plan,
                                    item,
                                    request,
                                    write,
                                    source.Adapter,
                                    cancellationToken).ConfigureAwait(false);
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
                                "Target write succeeded, but HaloPSA source writeback failed."));
                            failed++;
                            ReportProgress();
                            continue;
                        }
                        if (sourceWriteback is { Success: false })
                        {
                            results.Add(new EntitySyncApplyItemResult(
                                item.Action,
                                item.Source.Name,
                                item.Target?.Name,
                                false,
                                false,
                                write.Id,
                                sourceWriteback.Message ?? "Target write succeeded, but HaloPSA source writeback failed."));
                            failed++;
                            ReportProgress();
                            continue;
                        }

                        EntityWriteResult? replacementDelete = null;
                        if (BillComPlanReconciliation.IsReplacement(plan, item, write))
                        {
                            if (target.Adapter is not IEntityDeleteAdapter deleteAdapter)
                                throw new InvalidOperationException("BILL.com target connection does not support replacement cleanup.");
                            replacementDelete = await deleteAdapter
                                .DeleteEntityAsync(DeleteRequest(plan, item.Target!), cancellationToken)
                                .ConfigureAwait(false);
                            if (!replacementDelete.Success)
                            {
                                results.Add(new EntitySyncApplyItemResult(
                                    item.Action,
                                    item.Source.Name,
                                    item.Target?.Name,
                                    false,
                                    false,
                                    write.Id,
                                    replacementDelete.Message ?? "BILL.com replacement was created and written back to HaloPSA, but the old value could not be deleted."));
                                failed++;
                                ReportProgress();
                                continue;
                            }
                        }

                        var checkpointSucceeded = true;
                        if (changeStateRoute is not null)
                        {
                            try
                            {
                                await changeStates.UpsertAsync(new EntitySyncChangeState(
                                    changeStateRoute,
                                    item.Source.Id,
                                    item.Source.Name,
                                    write.Id ?? item.Target!.Id,
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
                                replacementDelete?.Message ?? sourceWriteback?.Message ?? write.Message ?? "Target write succeeded."));
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

    private static EntityWriteRequest DeleteRequest(EntitySyncPlan plan, ExternalEntity target) => new()
    {
        Vendor = plan.TargetVendor,
        EntityType = plan.TargetEntityType,
        Id = target.Id,
        Name = target.Name
    };
    private static async Task<EntityWriteResult?> WriteSourceIntegrationAsync(
        EntitySyncPlan plan,
        EntitySyncPlanItem item,
        EntityWriteRequest targetRequest,
        EntityWriteResult targetWrite,
        IEntityAdapter sourceAdapter,
        CancellationToken cancellationToken)
    {
        if (!plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(item.Source.Id))
        {
            return null;
        }

        var targetId = targetWrite.Id ?? item.Target?.Id;
        if (EntitySyncVendors.IsBillCom(plan.TargetVendor)
            && plan.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(targetId))
                return SourceWritebackFailure("BILL.com write succeeded, but no custom-field value ID was available for HaloPSA writeback.");

            var numericId = EntitySyncIntegrationContracts.DecodeBillComValueId(targetId);
            var writebackRequest = new EntityWriteRequest
            {
                Vendor = "HaloPSA",
                EntityType = "Client",
                Id = item.Source.Id,
                Name = item.Source.Name
            };
            writebackRequest.CustomFields[EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName] = numericId;
            var writeback = await sourceAdapter.UpdateEntityAsync(writebackRequest, cancellationToken).ConfigureAwait(false);
            return writeback.Success
                ? new EntityWriteResult
                {
                    Vendor = "HaloPSA",
                    EntityType = "Client",
                    Id = item.Source.Id,
                    Action = "BillComWriteBack",
                    Success = true,
                    Message = $"Recorded BILL.com client ID '{numericId}' on HaloPSA client '{item.Source.Name}'."
                }
                : SourceWritebackFailure("BILL.com value write succeeded, but HaloPSA client-ID writeback failed.");
        }

        if (!plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
            return null;
        if (sourceAdapter is not IHaloSourceWritebackAdapter haloWriteback)
            return SourceWritebackFailure("N-central write succeeded, but the HaloPSA connection cannot write integration links.");
        if (string.IsNullOrWhiteSpace(targetId))
            return SourceWritebackFailure("N-central write succeeded, but no target ID was available for HaloPSA integration-link writeback.");

        if (plan.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            && plan.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
        {
            var targetName = item.Target?.Name ?? EntitySyncIntegrationContracts.SanitizeNCentralName(targetRequest.Name);
            var writeback = await haloWriteback.UpsertNCentralClientLinkAsync(
                item.Source.Id,
                item.Source.Name,
                targetId,
                targetName,
                cancellationToken).ConfigureAwait(false);
            return writeback.Success
                ? writeback
                : SourceWritebackFailure("N-central customer write succeeded, but HaloPSA client-link writeback failed.");
        }

        if (plan.SourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && plan.TargetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            var customerId = targetRequest.CustomFields.TryGetValue("NCentralCustomerId", out var linkedCustomerId)
                ? linkedCustomerId
                : item.Source.GetExternalId("NCentralCustomerId");
            if (string.IsNullOrWhiteSpace(customerId))
                return SourceWritebackFailure("N-central site write succeeded, but no parent customer ID was available for HaloPSA site-link writeback.");

            var targetName = item.Target?.Name ?? EntitySyncIntegrationContracts.SanitizeNCentralName(targetRequest.Name);
            var haloClientName = item.Source.GetCustomField("HaloPsaClientName") ?? string.Empty;
            var writeback = await haloWriteback.UpsertNCentralSiteLinkAsync(
                item.Source.Id,
                item.Source.Name,
                haloClientName,
                targetId,
                targetName,
                customerId,
                cancellationToken).ConfigureAwait(false);
            return writeback.Success
                ? writeback
                : SourceWritebackFailure("N-central site write succeeded, but HaloPSA site-link writeback failed.");
        }

        return null;
    }

    private static EntityWriteResult SourceWritebackFailure(string message) => new()
    {
        Vendor = "HaloPSA",
        Success = false,
        Message = message
    };

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
