using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class VendorOutcomeReconciler(
    ISyncOperationRepository operations,
    IDurableSyncPlanRepository plans,
    ISyncPolicyRepository policies,
    IConnectionRuntimeFactory connections,
    IEntitySyncDataProtector protector,
    SyncAuditService audits,
    TimeProvider? timeProvider = null,
    TimeSpan? reconciliationLease = null)
{
    private readonly TimeSpan reconciliationLease =
        reconciliationLease ?? TimeSpan.FromMinutes(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<EntitySyncOperationItem?> ReconcileAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        var claim = await operations.TryLeaseUnknownItemAsync(
            tenantId, operationId, itemId, leaseOwner, reconciliationLease,
            cancellationToken).ConfigureAwait(false);
        if (claim is null) return null;
        var operation = await operations.GetAsync(
            tenantId, operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
            return null;
        var plan = await plans.GetAsync(
            tenantId, operation.PlanId, cancellationToken).ConfigureAwait(false);
        var policy = plan is null
            ? null
            : await policies.GetAsync(
                tenantId, plan.PolicyId, plan.PolicyVersion, cancellationToken)
                .ConfigureAwait(false);
        if (plan is null || policy is null
            || policy.DefinitionSha256 != plan.PolicyDefinitionSha256)
            return await ReleaseUnknownAsync(
                tenantId, claim, "CONTROL_STATE_UNAVAILABLE", cancellationToken)
                .ConfigureAwait(false);
        if (operation.RunId is null || operation.CorrelationId is null)
            return await ReleaseUnknownAsync(
                tenantId, claim, "AUDIT_CORRELATION_UNAVAILABLE",
                cancellationToken).ConfigureAwait(false);

        var snapshot = await operations.GetSnapshotAsync(
            tenantId, operationId, itemId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.EncryptedBeforeCiphertext is null)
            return await ReleaseUnknownAsync(
                tenantId, claim, "SNAPSHOT_UNAVAILABLE", cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, JsonElement> before;
        IReadOnlyDictionary<string, JsonElement> desired;
        try
        {
            var plaintext = protector.Unprotect(
                EntitySyncDataProtectionPurpose.AuditValue,
                snapshot.EncryptedBeforeCiphertext);
            using var document = JsonDocument.Parse(plaintext);
            before = ReadPayload(document.RootElement.GetProperty("before"));
            desired = ReadPayload(document.RootElement.GetProperty("desired"));
            if (PlanManifestBuilder.HashPayload(desired) != claim.Item.DesiredPayloadSha256
                || claim.Item.BeforePayloadSha256 is not null
                && PlanManifestBuilder.HashPayload(before) != claim.Item.BeforePayloadSha256)
                return await ReleaseUnknownAsync(
                    tenantId, claim, "SNAPSHOT_HASH_MISMATCH", cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await ReleaseUnknownAsync(
                tenantId, claim, "SNAPSHOT_UNAVAILABLE", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await using var targetLease = await connections.AcquireAsync(
                tenantId, operation.TargetConnectionId,
                operation.TargetConnectionGeneration, cancellationToken)
                .ConfigureAwait(false);
            var adapter = targetLease.Adapter;
            var lookupRequest = new EntityWriteRequest
            {
                Vendor = claim.Item.TargetVendor,
                EntityType = claim.Item.TargetEntityType,
                Id = claim.Item.VendorTargetEntityId ?? claim.Item.TargetEntityId,
                VendorRequestId = claim.Item.VendorRequestId,
                Correlation = new EntityWriteCorrelation(
                    operation.OperationId,
                    operation.PlanId,
                    operation.RunId.Value,
                    claim.Item.ItemIndex,
                    operation.CorrelationId.Value),
            };
            var requestLookup = claim.Item.VendorRequestId is null
                ? null
                : await adapter.LookupWriteByRequestIdAsync(
                    lookupRequest, cancellationToken).ConfigureAwait(false);
            if (requestLookup?.RequestLookupOutcome == VendorRequestLookupOutcome.NotApplied)
                return await CompleteAsync(
                    tenantId, operation, policy, claim, EntitySyncItemOutcome.Failed,
                    null, null, "VENDOR_PROVED_NOT_APPLIED", cancellationToken)
                    .ConfigureAwait(false);
            if (requestLookup?.RequestLookupOutcome == VendorRequestLookupOutcome.Applied)
            {
                var requestTargetId = requestLookup.Id
                    ?? claim.Item.VendorTargetEntityId
                    ?? claim.Item.TargetEntityId;
                var observed = requestTargetId is null
                    ? null
                    : await ReadExactAsync(
                        adapter, claim.Item.TargetEntityType, requestTargetId,
                        cancellationToken).ConfigureAwait(false);
                if (observed is null)
                    return await ReleaseUnknownAsync(
                        tenantId, claim, "REQUEST_ID_APPLIED_READBACK_PENDING",
                        cancellationToken).ConfigureAwait(false);
                var actual = PlanManifestBuilder.BuildBeforePayload(
                    observed, desired.Keys, policy.Definition.BlockedFields);
                return await CompleteAsync(
                    tenantId, operation, policy, claim, EntitySyncItemOutcome.Succeeded,
                    observed, actual, "REQUEST_ID_PROVED_APPLIED", cancellationToken)
                    .ConfigureAwait(false);
            }

            var immutableTargetId = claim.Item.VendorTargetEntityId
                ?? claim.Item.TargetEntityId;
            if (immutableTargetId is not null)
            {
                var observed = await ReadExactAsync(
                    adapter, claim.Item.TargetEntityType, immutableTargetId,
                    cancellationToken).ConfigureAwait(false);
                if (observed is not null)
                {
                    var actual = PlanManifestBuilder.BuildBeforePayload(
                        observed, desired.Keys, policy.Definition.BlockedFields);
                    var actualHash = PlanManifestBuilder.HashPayload(actual);
                    if (actualHash == claim.Item.DesiredPayloadSha256)
                        return await CompleteAsync(
                            tenantId, operation, policy, claim,
                            EntitySyncItemOutcome.Succeeded, observed, desired,
                            "TARGET_ID_PROVED_APPLIED", cancellationToken)
                            .ConfigureAwait(false);
                }
                return await ReleaseUnknownAsync(
                    tenantId, claim, "TARGET_ID_READBACK_INCONCLUSIVE",
                    cancellationToken).ConfigureAwait(false);
            }

            var exactMatches = await ReadExactDesiredMatchesAsync(
                adapter, claim.Item.TargetEntityType, desired, policy,
                cancellationToken).ConfigureAwait(false);
            if (exactMatches.Count == 1)
                return await CompleteAsync(
                    tenantId, operation, policy, claim,
                    EntitySyncItemOutcome.Succeeded, exactMatches[0], desired,
                    "DESIRED_STATE_PROVED_APPLIED", cancellationToken)
                    .ConfigureAwait(false);
            return await ReleaseUnknownAsync(
                tenantId, claim, "RECONCILIATION_INCONCLUSIVE", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await ReleaseUnknownAsync(
                tenantId, claim, "RECONCILIATION_INCONCLUSIVE", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private Task<bool> RenewLeaseAsync(
        string tenantId,
        UnknownItemLease claim,
        CancellationToken cancellationToken) =>
        operations.TryRenewUnknownItemLeaseAsync(
            tenantId, claim.Item.OperationId, claim.Item.ItemId,
            claim.ReconciliationAttempt, claim.LeaseOwner, reconciliationLease,
            cancellationToken);

    private async Task<EntitySyncOperationItem?> CompleteAsync(
        string tenantId,
        EntitySyncOperation operation,
        EntitySyncPolicy policy,
        UnknownItemLease claim,
        EntitySyncItemOutcome outcome,
        ExternalEntity? observed,
        IReadOnlyDictionary<string, JsonElement>? observedPayload,
        string safeCode,
        CancellationToken cancellationToken)
    {
        var completedAt = clock.GetUtcNow();
        EntitySyncOperationItemSnapshot? afterSnapshot = null;
        EntitySyncSha256? afterHash = null;
        if (observedPayload is not null)
        {
            afterHash = PlanManifestBuilder.HashPayload(observedPayload);
            afterSnapshot = new EntitySyncOperationItemSnapshot(
                tenantId, operation.OperationId, claim.Item.ItemId, null,
                protector.Protect(
                    EntitySyncDataProtectionPurpose.AuditValue,
                    PlanManifestBuilder.ToJsonValue(observedPayload).Json),
                claim.Item.SnapshotsExpireAt);
        }
        if (afterSnapshot is not null
            && !await operations.TryRecordReconciliationEvidenceAsync(
                tenantId, operation.OperationId, claim.Item.ItemId,
                claim.ReconciliationAttempt, claim.LeaseOwner, afterHash!,
                observed?.Id ?? claim.Item.VendorTargetEntityId, afterSnapshot,
                cancellationToken).ConfigureAwait(false))
            return null;
        var replacement = Copy(
            claim.Item, outcome, completedAt, afterHash,
            observed?.Id ?? claim.Item.VendorTargetEntityId, safeCode);
        if (outcome != EntitySyncItemOutcome.Succeeded)
        {
            var persisted = await operations.TryCompleteReconciliationAsync(
                tenantId, operation.OperationId, claim.Item.ItemId,
                claim.ReconciliationAttempt, claim.LeaseOwner, replacement,
                null, cancellationToken).ConfigureAwait(false);
            return persisted ? replacement : null;
        }
        EntitySyncChangeState? checkpoint = null;
        if (policy.Definition.UpdatePolicy == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly)
        {
            var route = EntitySyncChangeStateRoute.Create(
                tenantId, operation.RouteScope, claim.Item.SourceVendor,
                claim.Item.SourceConnectionId, claim.Item.SourceEntityType,
                claim.Item.TargetVendor, claim.Item.TargetConnectionId,
                claim.Item.TargetEntityType);
            checkpoint = new EntitySyncChangeState(
                route, claim.Item.SourceEntityId, claim.Item.SourceEntityId,
                observed?.Id ?? claim.Item.VendorTargetEntityId
                    ?? claim.Item.TargetEntityId
                    ?? throw new InvalidOperationException(
                        "A reconciled changed-only update requires a target ID."),
                EntityWriteRequestDigest.SchemaVersion,
                claim.Item.DesiredPayloadSha256.Value,
                completedAt);
        }
        var audit = audits.Prepare(
            tenantId,
            "SyncOperationItemSucceeded",
            new EntitySyncActor("entitysync-worker"),
            operation.OperationId,
            operation.PlanId,
            claim.Item.ItemId,
            claim.Item.VendorRequestId ?? claim.Item.ItemId.ToString("N"),
            new
            {
                claim.Item.ItemId,
                Outcome = outcome.ToString(),
                claim.Item.DesiredPayloadSha256,
                AfterPayloadSha256 = afterHash,
                SafeCode = safeCode
            },
            observed);
        var committed = await operations.TryCommitReconciliationSuccessAsync(
            tenantId, operation.OperationId, claim.Item.ItemId,
            claim.ReconciliationAttempt, claim.LeaseOwner,
            replacement, checkpoint, audit.Event, audit.FullValues,
            cancellationToken).ConfigureAwait(false);
        return committed ? replacement : null;
    }

    private async Task<EntitySyncOperationItem?> ReleaseUnknownAsync(
        string tenantId,
        UnknownItemLease claim,
        string safeCode,
        CancellationToken cancellationToken)
    {
        var replacement = Copy(
            claim.Item, EntitySyncItemOutcome.Unknown,
            claim.Item.CompletedAt ?? clock.GetUtcNow(), claim.Item.AfterPayloadSha256,
            claim.Item.VendorTargetEntityId, safeCode);
        var persisted = await operations.TryCompleteReconciliationAsync(
            tenantId, claim.Item.OperationId, claim.Item.ItemId,
            claim.ReconciliationAttempt, claim.LeaseOwner, replacement, null,
            cancellationToken).ConfigureAwait(false);
        return persisted ? replacement : null;
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

    private static async Task<IReadOnlyList<ExternalEntity>> ReadExactDesiredMatchesAsync(
        IEntityAdapter adapter,
        string entityType,
        IReadOnlyDictionary<string, JsonElement> desired,
        EntitySyncPolicy policy,
        CancellationToken cancellationToken)
    {
        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery
            {
                EntityType = entityType,
                Count = 5000,
                FullObjects = true,
                IncludeInactive = true
            }, cancellationToken).ConfigureAwait(false);
        var desiredHash = PlanManifestBuilder.HashPayload(desired);
        return entities.Where(entity =>
            PlanManifestBuilder.HashPayload(
                PlanManifestBuilder.BuildBeforePayload(
                    entity, desired.Keys, policy.Definition.BlockedFields)) == desiredHash)
            .Take(2)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadPayload(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Snapshot payload must be an object.");
        return value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static EntitySyncOperationItem Copy(
        EntitySyncOperationItem item,
        EntitySyncItemOutcome outcome,
        DateTimeOffset completedAt,
        EntitySyncSha256? afterPayloadSha256,
        string? vendorTargetEntityId,
        string? safeCode)
    {
        var replacement = new EntitySyncOperationItem(
            item.TenantId, item.OperationId, item.PlanId, item.ItemId, item.ItemIndex,
            item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, item.RedactedBefore, item.RedactedDesired,
            item.BeforePayloadSha256, item.DesiredPayloadSha256,
            afterPayloadSha256, item.SnapshotsExpireAt, item.VendorRequestId,
            outcome,
            outcome == EntitySyncItemOutcome.Failed ? safeCode : null,
            outcome == EntitySyncItemOutcome.Failed
                ? "Vendor evidence proved that the requested state was not applied."
                : null,
            item.StartedAt,
            completedAt,
            item.ResolvedTargetParent);
        return replacement with
        {
            DispatchStartedAt = item.DispatchStartedAt,
            VendorTargetEntityId = vendorTargetEntityId,
            SafeWriteCode = safeCode
        };
    }
}
