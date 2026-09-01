using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public interface IEntitySyncOperationRouteLease : IAsyncDisposable
{
    Task<bool> TryRenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken);
}

public interface IEntitySyncOperationRouteLock
{
    Task<IEntitySyncOperationRouteLease?> TryAcquireAsync(
        EntitySyncOperation operation,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}

public sealed class EntitySyncOperationWorker(
    ISyncOperationRepository operations,
    IDurableSyncPlanRepository plans,
    ISyncPolicyRepository policies,
    IConnectionRuntimeFactory connections,
    IEntityMapper mapper,
    IEntitySyncDataProtector protector,
    VendorOutcomeReconciler reconciler,
    SyncAuditService audits,
    TimeProvider? timeProvider = null,
    EntitySyncOperationWorkerOptions? options = null,
    IEntitySyncOperationRouteLock? operationRouteLock = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly EntitySyncOperationWorkerOptions workerOptions =
        options ?? EntitySyncOperationWorkerOptions.Default;

    public async Task<EntitySyncOperation?> ExecuteOneAsync(
        string tenantId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var operation = await operations.TryLeaseNextAsync(
            tenantId, leaseOwner, now, now + workerOptions.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (operation is null) return null;
        await using var routeLease = operationRouteLock is null
            ? null
            : await operationRouteLock.TryAcquireAsync(
                operation, leaseOwner + ":route", workerOptions.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
        if (operationRouteLock is not null && routeLease is null) return operation;
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = ownership.Token;
        var running = operation.Start(clock.GetUtcNow());
        if (!await operations.TryReplaceAsync(
                tenantId, operation.OperationId, EntitySyncOperationStatus.Leased,
                running, cancellationToken).ConfigureAwait(false))
            return null;
        var ownershipRenewal = MaintainOwnershipAsync(
            running, leaseOwner, routeLease, ownership);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await operations.GetItemsAsync(
                tenantId, running.OperationId, cancellationToken).ConfigureAwait(false);
            var item = items.FirstOrDefault(candidate =>
                           candidate.Outcome == EntitySyncItemOutcome.Pending)
                       ?? items.FirstOrDefault(candidate =>
                           candidate.Outcome == EntitySyncItemOutcome.Unknown);
            if (item is null)
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            if (item.Outcome == EntitySyncItemOutcome.Unknown)
            {
                await reconciler.ReconcileAsync(
                    tenantId, running.OperationId, item.ItemId,
                    leaseOwner + ":reconcile", cancellationToken).ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }
            if (item.DispatchStartedAt is not null)
            {
                var unknown = VendorOutcomeReconciler.Copy(
                    item, EntitySyncItemOutcome.Unknown, clock.GetUtcNow(),
                    item.AfterPayloadSha256, item.VendorTargetEntityId,
                    "RECLAIMED_AFTER_DISPATCH");
                await operations.TryRecordItemAsync(
                    tenantId, running.OperationId, running.PlanId, item.ItemId,
                    running.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
                    unknown, null, CancellationToken.None).ConfigureAwait(false);
                await reconciler.ReconcileAsync(
                    tenantId, running.OperationId, item.ItemId,
                    leaseOwner + ":reconcile", CancellationToken.None)
                    .ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            var plan = await plans.GetAsync(
                tenantId, running.PlanId, cancellationToken).ConfigureAwait(false)
                ?? throw new DurablePlanNotFoundException(running.PlanId);
            var policy = await policies.GetAsync(
                tenantId, plan.PolicyId, plan.PolicyVersion, cancellationToken)
                .ConfigureAwait(false);
            if (policy is null || !policy.Enabled
                || policy.DefinitionSha256 != plan.PolicyDefinitionSha256)
            {
                await FailBeforeDispatchAsync(
                    tenantId, running, item, leaseOwner, "POLICY_CHANGED")
                    .ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            await using var sourceLease = await connections.AcquireAsync(
                tenantId, running.SourceConnectionId,
                running.SourceConnectionGeneration, cancellationToken)
                .ConfigureAwait(false);
            await using var targetLease = await connections.AcquireAsync(
                tenantId, running.TargetConnectionId,
                running.TargetConnectionGeneration, cancellationToken)
                .ConfigureAwait(false);
            var source = await ReadExactAsync(
                sourceLease.Adapter, item.SourceEntityType, item.SourceEntityId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The immutable source entity was unavailable before dispatch.");
            ExternalEntity? targetBefore = null;
            if (item.TargetEntityId is not null)
            {
                targetBefore = await ReadExactAsync(
                    targetLease.Adapter, item.TargetEntityType, item.TargetEntityId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The immutable target entity was unavailable before dispatch.");
            }
            var resolvedParent = await ResolveCreateParentAsync(
                    item, targetLease.Adapter, cancellationToken)
                .ConfigureAwait(false);
            var writeRequest = CreateWriteRequest(
                item, source, targetBefore, policy, resolvedParent);
            var desired = PlanManifestBuilder.CreateAllowedDesiredPayload(
                writeRequest, policy.Definition);
            if (PlanManifestBuilder.HashPayload(desired) != item.DesiredPayloadSha256)
                throw new InvalidOperationException(
                    "Live source mapping no longer matches the approved desired-state hash.");
            var before = PlanManifestBuilder.BuildBeforePayload(
                targetBefore, desired.Keys, policy.Definition.BlockedFields);
            var beforeHash = targetBefore is null
                ? null
                : PlanManifestBuilder.HashPayload(before);
            var redactedBefore = PlanManifestBuilder.ToJsonValue(
                PlanManifestBuilder.Redact(before));
            var encryptedEvidence = protector.Protect(
                EntitySyncDataProtectionPurpose.AuditValue,
                JsonSerializer.Serialize(new { before, desired }));
            var evidenceSnapshot = new EntitySyncOperationItemSnapshot(
                tenantId, running.OperationId, item.ItemId, encryptedEvidence, null,
                item.SnapshotsExpireAt);

            if (running.Mode == EntitySyncOperationMode.DryRun
                || item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)
                || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
            {
                var outcome = item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)
                              || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase)
                    ? EntitySyncItemOutcome.Skipped
                    : EntitySyncItemOutcome.Succeeded;
                var dryRun = Copy(
                    item, redactedBefore, beforeHash, null, null,
                    outcome, clock.GetUtcNow(), "DRY_RUN_NO_VENDOR_WRITE");
                await audits.AppendAsync(
                    tenantId, "SyncOperationItemDryRun",
                    new EntitySyncActor("entitysync-worker"), running.OperationId,
                    running.PlanId, item.ItemId, item.ItemId.ToString("N"),
                    new
                    {
                        item.ItemId,
                        Outcome = outcome.ToString(),
                        item.DesiredPayloadSha256,
                        BeforePayloadSha256 = beforeHash
                    },
                    new { before, desired }, cancellationToken).ConfigureAwait(false);
                await operations.TryRecordItemAsync(
                    tenantId, running.OperationId, running.PlanId, item.ItemId,
                    running.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
                    dryRun, evidenceSnapshot, cancellationToken).ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var dispatchAt = clock.GetUtcNow();
            var vendorRequestId = CreateVendorRequestId(
                running.OperationId, item.ItemId);
            writeRequest.VendorRequestId = vendorRequestId;
            writeRequest.IdempotencyKey = vendorRequestId;
            var prepared = Copy(
                item, redactedBefore, beforeHash, vendorRequestId, dispatchAt,
                EntitySyncItemOutcome.Pending, null, null);
            var boundary = await operations.TryPrepareDispatchAsync(
                tenantId, running.OperationId, running.PlanId, item.ItemId,
                running.Attempt, leaseOwner, plan.PolicyId, plan.PolicyVersion,
                plan.PolicyDefinitionSha256, prepared, evidenceSnapshot,
                cancellationToken).ConfigureAwait(false);
            if (boundary.Outcome != DispatchPreparationOutcome.Prepared)
            {
                if (boundary.Outcome == DispatchPreparationOutcome.AlreadyDispatchStarted)
                {
                    var current = await operations.GetItemAsync(
                        tenantId, running.OperationId, item.ItemId,
                        CancellationToken.None).ConfigureAwait(false);
                    if (current?.Outcome == EntitySyncItemOutcome.Pending)
                    {
                        var unknown = VendorOutcomeReconciler.Copy(
                            current, EntitySyncItemOutcome.Unknown, clock.GetUtcNow(),
                            current.AfterPayloadSha256, current.VendorTargetEntityId,
                            "DISPATCH_STATE_ALREADY_STARTED");
                        await operations.TryRecordItemAsync(
                            tenantId, running.OperationId, running.PlanId, item.ItemId,
                            running.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
                            unknown, null, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else if (boundary.Outcome == DispatchPreparationOutcome.Excluded)
                {
                    var skipped = Copy(
                        item, redactedBefore, beforeHash, null, null,
                        EntitySyncItemOutcome.Skipped, clock.GetUtcNow(),
                        "CREATE_EXCLUDED_BEFORE_DISPATCH");
                    await operations.TryRecordItemAsync(
                        tenantId, running.OperationId, running.PlanId, item.ItemId,
                        running.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
                        skipped, evidenceSnapshot, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (boundary.Outcome is DispatchPreparationOutcome.PolicyChanged
                         or DispatchPreparationOutcome.ConnectionChanged)
                {
                    await FailBeforeDispatchAsync(
                        tenantId, running, item, leaseOwner,
                        boundary.Outcome.ToString().ToUpperInvariant())
                        .ConfigureAwait(false);
                }
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            EntityWriteResult writeResult;
            try
            {
                writeResult = item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
                    ? await targetLease.Adapter.CreateEntityAsync(
                        writeRequest, cancellationToken).ConfigureAwait(false)
                    : await targetLease.Adapter.UpdateEntityAsync(
                        writeRequest, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await PersistUnknownAsync(
                    tenantId, running, prepared, leaseOwner, null, null,
                    "VENDOR_RESPONSE_UNKNOWN").ConfigureAwait(false);
                await reconciler.ReconcileAsync(
                    tenantId, running.OperationId, item.ItemId,
                    leaseOwner + ":reconcile", CancellationToken.None)
                    .ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            if (!writeResult.Success)
            {
                var failed = VendorOutcomeReconciler.Copy(
                    prepared, EntitySyncItemOutcome.Failed, clock.GetUtcNow(), null,
                    writeResult.Id, writeResult.SafeCode ?? "VENDOR_REJECTED_WRITE");
                await operations.TryRecordItemAsync(
                    tenantId, running.OperationId, running.PlanId, item.ItemId,
                    running.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
                    failed, null, CancellationToken.None).ConfigureAwait(false);
                return await FinalizeAsync(tenantId, running, leaseOwner)
                    .ConfigureAwait(false);
            }

            ExternalEntity? observed = null;
            IReadOnlyDictionary<string, JsonElement>? after = null;
            try
            {
                var targetId = writeResult.Id ?? item.TargetEntityId;
                if (targetId is not null)
                    observed = await ReadExactAsync(
                        targetLease.Adapter, item.TargetEntityType, targetId,
                        CancellationToken.None).ConfigureAwait(false);
                if (observed is not null)
                    after = PlanManifestBuilder.BuildBeforePayload(
                        observed, desired.Keys, policy.Definition.BlockedFields);
            }
            catch
            {
                // The committed dispatch boundary forbids a retry; reconciliation owns uncertainty.
            }
            var reconciledItem = prepared with
            {
                VendorTargetEntityId = writeResult.Id ?? prepared.VendorTargetEntityId
            };
            await PersistUnknownAsync(
                tenantId, running, reconciledItem, leaseOwner, observed, after,
                "WRITE_REQUIRES_RECONCILIATION").ConfigureAwait(false);
            await reconciler.ReconcileAsync(
                tenantId, running.OperationId, item.ItemId,
                leaseOwner + ":reconcile", CancellationToken.None)
                .ConfigureAwait(false);
            return await FinalizeAsync(tenantId, running, leaseOwner)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var currentItems = await operations.GetItemsAsync(
                tenantId, running.OperationId, CancellationToken.None).ConfigureAwait(false);
            if (currentItems.All(item => item.DispatchStartedAt is null))
                return await operations.TryCancelAttemptAsync(
                    tenantId, running.OperationId, running.Attempt, leaseOwner,
                    clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            return await FinalizeAsync(tenantId, running, leaseOwner).ConfigureAwait(false);
        }
        catch (EntityWriteParentValidationException exception)
        {
            var current = await operations.GetItemsAsync(
                tenantId, running.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
            var pending = current.FirstOrDefault(
                value => value.Outcome == EntitySyncItemOutcome.Pending);
            if (pending is not null && pending.DispatchStartedAt is null)
                await FailBeforeDispatchAsync(
                    tenantId,
                    running,
                    pending,
                    leaseOwner,
                    exception.SafeCode).ConfigureAwait(false);
            return await FinalizeAsync(tenantId, running, leaseOwner)
                .ConfigureAwait(false);
        }
        catch (UnsupportedEntityWriteParentMappingException exception)
        {
            var current = await operations.GetItemsAsync(
                tenantId, running.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
            var pending = current.FirstOrDefault(
                value => value.Outcome == EntitySyncItemOutcome.Pending);
            if (pending is not null && pending.DispatchStartedAt is null)
                await FailBeforeDispatchAsync(
                    tenantId,
                    running,
                    pending,
                    leaseOwner,
                    exception.SafeCode).ConfigureAwait(false);
            return await FinalizeAsync(tenantId, running, leaseOwner)
                .ConfigureAwait(false);
        }
        catch
        {
            var current = await operations.GetItemsAsync(
                tenantId, running.OperationId, CancellationToken.None).ConfigureAwait(false);
            var pending = current.FirstOrDefault(item => item.Outcome == EntitySyncItemOutcome.Pending);
            if (pending is not null && pending.DispatchStartedAt is null)
                await FailBeforeDispatchAsync(
                    tenantId, running, pending, leaseOwner, "PRE_DISPATCH_VALIDATION_FAILED")
                    .ConfigureAwait(false);
            else if (pending is not null)
                await PersistUnknownAsync(
                    tenantId, running, pending, leaseOwner, null, null,
                    "WORKER_FAILED_AFTER_DISPATCH").ConfigureAwait(false);
            return await FinalizeAsync(tenantId, running, leaseOwner).ConfigureAwait(false);
        }
        finally
        {
            await ownership.CancelAsync().ConfigureAwait(false);
            try
            {
                await ownershipRenewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ownership.IsCancellationRequested)
            {
            }
        }
    }

    private async Task MaintainOwnershipAsync(
        EntitySyncOperation operation,
        string leaseOwner,
        IEntitySyncOperationRouteLease? routeLease,
        CancellationTokenSource ownership)
    {
        try
        {
            var interval = TimeSpan.FromTicks(workerOptions.LeaseDuration.Ticks / 3);
            while (!ownership.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, ownership.Token).ConfigureAwait(false);
                var operationRenewed = await operations.TryRenewLeaseAsync(
                    operation.TenantId,
                    operation.OperationId,
                    operation.Attempt,
                    leaseOwner,
                    workerOptions.LeaseDuration,
                    ownership.Token).ConfigureAwait(false);
                var routeRenewed = routeLease is null
                    || await routeLease.TryRenewAsync(
                        workerOptions.LeaseDuration, ownership.Token).ConfigureAwait(false);
                if (!operationRenewed || !routeRenewed)
                {
                    await ownership.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await ownership.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static string CreateVendorRequestId(Guid operationId, Guid itemId)
    {
        var input = Encoding.UTF8.GetBytes(
            $"entitysync:vendor-request:v1:{operationId:N}:{itemId:N}");
        return "es_" + Convert.ToHexString(SHA256.HashData(input))[..40].ToLowerInvariant();
    }

    private async Task PersistUnknownAsync(
        string tenantId,
        EntitySyncOperation operation,
        EntitySyncOperationItem item,
        string leaseOwner,
        ExternalEntity? observed,
        IReadOnlyDictionary<string, JsonElement>? after,
        string safeCode)
    {
        var afterHash = after is null ? null : PlanManifestBuilder.HashPayload(after);
        EntitySyncOperationItemSnapshot? snapshot = null;
        if (after is not null)
        {
            snapshot = new EntitySyncOperationItemSnapshot(
                tenantId, operation.OperationId, item.ItemId, null,
                protector.Protect(
                    EntitySyncDataProtectionPurpose.AuditValue,
                    PlanManifestBuilder.ToJsonValue(after).Json),
                item.SnapshotsExpireAt);
        }
        var unknown = VendorOutcomeReconciler.Copy(
            item, EntitySyncItemOutcome.Unknown, clock.GetUtcNow(), afterHash,
            observed?.Id ?? item.VendorTargetEntityId, safeCode);
        await operations.TryRecordItemAsync(
            tenantId, operation.OperationId, operation.PlanId, item.ItemId,
            operation.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
            unknown, snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailBeforeDispatchAsync(
        string tenantId,
        EntitySyncOperation operation,
        EntitySyncOperationItem item,
        string leaseOwner,
        string safeCode)
    {
        var failed = VendorOutcomeReconciler.Copy(
            item, EntitySyncItemOutcome.Failed, clock.GetUtcNow(), null, null,
            safeCode);
        await operations.TryRecordItemAsync(
            tenantId, operation.OperationId, operation.PlanId, item.ItemId,
            operation.Attempt, leaseOwner, EntitySyncItemOutcome.Pending,
            failed, null, CancellationToken.None).ConfigureAwait(false);
    }

    private Task<EntitySyncOperation?> FinalizeAsync(
        string tenantId,
        EntitySyncOperation operation,
        string leaseOwner) =>
        operations.TryFinalizeAttemptAsync(
            tenantId, operation.OperationId, operation.Attempt, leaseOwner,
            clock.GetUtcNow(), CancellationToken.None);

    internal static async Task<EntityWriteParent?> ResolveCreateParentAsync(
        EntitySyncOperationItem item,
        IEntityAdapter targetAdapter,
        CancellationToken cancellationToken)
    {
        if (!item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
            || !EntitySyncVendors.IsOrchestraMSP(item.TargetVendor)
            || (!item.TargetEntityType.Equals(
                    "Site", StringComparison.OrdinalIgnoreCase)
                && !item.TargetEntityType.Equals(
                    "Address", StringComparison.OrdinalIgnoreCase)))
            return null;
        var approved = item.ResolvedTargetParent
            ?? throw new EntityWriteParentValidationException(
                "ORCHESTRA_PARENT_EVIDENCE_MISSING");
        if (targetAdapter is not IEntityWriteParentResolver resolver)
            throw new EntityWriteParentValidationException(
                "ORCHESTRA_PARENT_RESOLVER_UNAVAILABLE");
        var current = await resolver.ResolveWriteParentAsync(
            new EntityWriteParentResolutionRequest(
                item.SourceVendor,
                item.SourceConnectionId,
                approved.ParentEntityType,
                approved.MatchedLinkExternalId),
            cancellationToken).ConfigureAwait(false);
        if (current.Status != EntityWriteParentResolutionStatus.Resolved
            || current.Parent is null)
            throw new EntityWriteParentValidationException(current.SafeCode);
        if (!SameParentEvidence(approved, current.Parent))
            throw new EntityWriteParentValidationException(
                "ORCHESTRA_PARENT_EVIDENCE_CHANGED");
        return current.Parent;
    }

    private static bool SameParentEvidence(
        EntityWriteParent approved,
        EntityWriteParent current) =>
        approved.ClientId == current.ClientId
        && approved.SiteId == current.SiteId
        && approved.ParentEntityType.Equals(
            current.ParentEntityType, StringComparison.OrdinalIgnoreCase)
        && approved.SourcePlatformInstanceId.Equals(
            current.SourcePlatformInstanceId, StringComparison.Ordinal)
        && approved.MatchedLinkExternalId.Equals(
            current.MatchedLinkExternalId, StringComparison.Ordinal)
        && approved.MatchedLinkStatus.Equals(
            current.MatchedLinkStatus, StringComparison.OrdinalIgnoreCase)
        && approved.MatchedLinkToken.Equals(
            current.MatchedLinkToken, StringComparison.Ordinal)
        && approved.ObservedOwnerVersion == current.ObservedOwnerVersion;

    private EntityWriteRequest CreateWriteRequest(
        EntitySyncOperationItem item,
        ExternalEntity source,
        ExternalEntity? target,
        EntitySyncPolicy policy,
        EntityWriteParent? resolvedParent)
    {
        var matchOptions = new MatchOptions
        {
            SourceExternalIdName = policy.Definition.SourceExternalIdName ?? "Id",
            TargetExternalIdName = policy.Definition.SourceExternalIdName ?? "Id",
            TargetCustomFieldName = policy.Definition.TargetCustomFieldName ?? string.Empty,
            AutoLinkScore = policy.Definition.AutoLinkScore,
            ReviewScore = policy.Definition.ReviewScore,
            CreateMissing = policy.Definition.CreateMissing
        };
        var request = item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
            ? mapper.MapCreate(
                source,
                item.TargetVendor,
                item.TargetEntityType,
                matchOptions,
                resolvedParent)
            : mapper.MapUpdate(
                source,
                target ?? throw new InvalidOperationException(
                    "Update dispatch requires an immutable target snapshot."),
                matchOptions);
        var allowed = policy.Definition.AllowedFields;
        request.Fields = request.Fields
            .Where(pair => allowed.Contains(pair.Key)
                           && !policy.Definition.BlockedFields.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        request.CustomFields = request.CustomFields
            .Where(pair => allowed.Contains(pair.Key)
                           && !policy.Definition.BlockedFields.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains("primarySiteId")) request.PrimarySiteId = null;
        if (!allowed.Contains("name")) request.Name = string.Empty;
        return request;
    }

    private static EntitySyncOperationItem Copy(
        EntitySyncOperationItem item,
        EntitySyncJsonValue redactedBefore,
        EntitySyncSha256? beforeHash,
        string? vendorRequestId,
        DateTimeOffset? dispatchStartedAt,
        EntitySyncItemOutcome outcome,
        DateTimeOffset? completedAt,
        string? safeCode)
    {
        var replacement = new EntitySyncOperationItem(
            item.TenantId, item.OperationId, item.PlanId, item.ItemId,
            item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, redactedBefore, item.RedactedDesired, beforeHash,
            item.DesiredPayloadSha256, item.AfterPayloadSha256,
            item.SnapshotsExpireAt, vendorRequestId, outcome,
            null, null, dispatchStartedAt ?? item.StartedAt, completedAt,
            item.ResolvedTargetParent);
        return replacement with
        {
            DispatchStartedAt = dispatchStartedAt,
            VendorTargetEntityId = item.VendorTargetEntityId,
            SafeWriteCode = safeCode
        };
    }

    private static async Task<ExternalEntity?> ReadExactAsync(
        IEntityAdapter adapter,
        string entityType,
        string id,
        CancellationToken cancellationToken)
    {
        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery
            {
                EntityType = entityType,
                Search = id,
                Count = 100,
                FullObjects = true,
                IncludeInactive = true
            }, cancellationToken).ConfigureAwait(false);
        return entities.SingleOrDefault(
            entity => string.Equals(entity.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class EntityWriteParentValidationException(string safeCode)
    : InvalidOperationException("Approved parent evidence is no longer current.")
{
    public string SafeCode { get; } = string.IsNullOrWhiteSpace(safeCode)
        ? "ORCHESTRA_PARENT_EVIDENCE_CHANGED"
        : safeCode;
}

public sealed record EntitySyncOperationWorkerOptions(TimeSpan LeaseDuration)
{
    public static EntitySyncOperationWorkerOptions Default { get; } =
        new(TimeSpan.FromMinutes(5));
}
