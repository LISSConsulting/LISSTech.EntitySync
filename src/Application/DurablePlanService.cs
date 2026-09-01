using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class DurablePlanService(
    EntitySyncPlanner planner,
    PlanManifestBuilder manifestBuilder,
    ISyncPolicyRepository policies,
    IConnectionDefinitionRepository connectionDefinitions,
    IConnectionRuntimeFactory connections,
    IEntityExclusionRepository exclusions,
    IDurableSyncPlanRepository plans,
    TimeProvider timeProvider,
    DurablePlanCreationOptions? creationOptions = null)
{
    private readonly DurablePlanCreationOptions creationOptions =
        DurablePlanCreationOptions.Validate(
            creationOptions ?? DurablePlanCreationOptions.Default);

    public async Task<DurablePlanResult> CreatePlanAsync(
        CreateDurablePlanRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);
        ArgumentNullException.ThrowIfNull(actor);
        var tenantId = request.TenantId.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var planId = CreatePlanId(tenantId, idempotencyKey);
        var selection = new EntitySyncSelectionBounds(
            request.SourceSearch,
            request.SourceCount,
            request.SourceEntityId);
        var requestSha256 = ComputeCreateRequestSha256(
            request, tenantId, idempotencyKey, selection, actor);
        var ownerToken = Guid.NewGuid();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = await plans.TryClaimCreationAsync(
                tenantId,
                planId,
                requestSha256,
                ownerToken,
                creationOptions.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            switch (claim.State)
            {
                case DurablePlanCreationClaimState.Conflict:
                    throw new DurablePlanIdempotencyConflictException(planId);
                case DurablePlanCreationClaimState.Completed:
                    if (claim.ResultPlanId != planId
                        || claim.ResultPlanDigestSha256 is null)
                        throw new DurablePlanCreationConflictException(planId);
                    var completed = await plans.GetAsync(
                        tenantId, planId, cancellationToken).ConfigureAwait(false);
                    if (completed is null
                        || completed.PlanDigestSha256 != claim.ResultPlanDigestSha256)
                        throw new DurablePlanCreationConflictException(planId);
                    return ToResult(completed);
                case DurablePlanCreationClaimState.Waiting:
                    await Task.Delay(
                        creationOptions.PollInterval,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                case DurablePlanCreationClaimState.Owner:
                    try
                    {
                        return await RunWithCreationLeaseAsync(
                            token => CreateOwnedPlanAsync(
                                request,
                                actor,
                                tenantId,
                                planId,
                                selection,
                                requestSha256,
                                ownerToken,
                                token),
                            tenantId,
                            planId,
                            requestSha256,
                            ownerToken,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        try
                        {
                            await plans.ReleaseCreationAsync(
                                tenantId,
                                planId,
                                requestSha256,
                                ownerToken,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Preserve the creation failure. The finite claim lease remains
                            // recoverable even if immediate release cannot reach PostgreSQL.
                        }
                        throw;
                    }
                default:
                    throw new InvalidOperationException(
                        "The durable creation claim state is invalid.");
            }
        }
    }
    public async Task<EntitySyncDurablePlan> ImportManifestAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        string idempotencyKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(actor);
        tenantId = tenantId.Trim();
        var verified = EntitySyncDurablePlanManifest.LoadPersisted(
            manifest.Plan, manifest.Items);
        var plan = verified.Plan;
        if (plan.TenantId != tenantId)
            throw new InvalidOperationException(
                "Imported durable plan tenant does not match the control tenant.");
        if (plan.Status != EntitySyncDurablePlanStatus.Draft)
            throw new InvalidOperationException(
                "Only immutable Draft durable plans can be imported.");

        var policy = await policies.GetLatestAsync(
                tenantId, plan.PolicyId, cancellationToken).ConfigureAwait(false);
        if (policy is null
            || !policy.Enabled
            || policy.Version != plan.PolicyVersion
            || policy.DefinitionSha256 != plan.PolicyDefinitionSha256
            || policy.RouteScope != plan.RouteScope
            || policy.Definition.SourceConnectionId != plan.SourceConnectionId
            || policy.Definition.TargetConnectionId != plan.TargetConnectionId)
            throw new DurablePlanPolicyChangedException(plan.PlanId);
        await RequireCurrentConnectionAsync(
            tenantId,
            plan.SourceConnectionId,
            plan.SourceConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
        await RequireCurrentConnectionAsync(
            tenantId,
            plan.TargetConnectionId,
            plan.TargetConnectionGeneration,
            cancellationToken).ConfigureAwait(false);

        var result = await plans.ImportAsync(
                tenantId,
                verified,
                idempotencyKey.Trim(),
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        return result.State switch
        {
            DurablePlanImportPersistenceState.Inserted
                or DurablePlanImportPersistenceState.Replayed =>
                result.Plan
                ?? throw new InvalidOperationException(
                    "The imported durable plan is unavailable."),
            DurablePlanImportPersistenceState.Conflict =>
                throw new DurablePlanIdempotencyConflictException(plan.PlanId),
            DurablePlanImportPersistenceState.PolicyChanged =>
                throw new DurablePlanPolicyChangedException(plan.PlanId),
            DurablePlanImportPersistenceState.ConnectionChanged =>
                throw new DurablePlanConnectionChangedException(
                    plan.SourceConnectionId),
            _ => throw new InvalidOperationException(
                "The durable plan import result is invalid.")
        };
    }


    private async Task<DurablePlanResult> RunWithCreationLeaseAsync(
        Func<CancellationToken, Task<DurablePlanResult>> create,
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        using var ownershipCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var createTask = create(ownershipCancellation.Token);
        var renewalTask = MaintainCreationLeaseAsync(
            tenantId,
            planId,
            requestSha256,
            ownerToken,
            ownershipCancellation.Token);
        var first = await Task.WhenAny(createTask, renewalTask).ConfigureAwait(false);
        if (first == createTask)
        {
            try
            {
                return await createTask.ConfigureAwait(false);
            }
            finally
            {
                ownershipCancellation.Cancel();
                await IgnoreLeaseCleanupAsync(renewalTask).ConfigureAwait(false);
            }
        }

        try
        {
            await renewalTask.ConfigureAwait(false);
            return await createTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ownershipCancellation.Cancel();
            await IgnoreLeaseCleanupAsync(createTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            ownershipCancellation.Cancel();
            await IgnoreLeaseCleanupAsync(createTask).ConfigureAwait(false);
            throw new DurablePlanCreationConflictException(planId, exception);
        }
    }

    private async Task MaintainCreationLeaseAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                creationOptions.RenewalInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
            if (await plans.RenewCreationAsync(
                    tenantId,
                    planId,
                    requestSha256,
                    ownerToken,
                    creationOptions.LeaseDuration,
                    cancellationToken).ConfigureAwait(false))
                continue;

            var observed = await plans.TryClaimCreationAsync(
                tenantId,
                planId,
                requestSha256,
                ownerToken,
                creationOptions.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (observed.State == DurablePlanCreationClaimState.Completed)
                return;
            if (observed.State != DurablePlanCreationClaimState.Owner)
                throw new DurablePlanCreationConflictException(planId);
        }
    }

    private static async Task IgnoreLeaseCleanupAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Cleanup observes the losing task so stale planner work cannot continue.
        }
    }

    private async Task<DurablePlanResult> CreateOwnedPlanAsync(
        CreateDurablePlanRequest request,
        EntitySyncActor actor,
        string tenantId,
        Guid planId,
        EntitySyncSelectionBounds selection,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        if (await plans.GetAsync(tenantId, planId, cancellationToken)
                .ConfigureAwait(false) is not null)
            throw new DurablePlanCreationConflictException(planId);
        var policy = await ResolveCurrentPolicyAsync(
            tenantId, request.PolicyId, request.PolicyVersion, cancellationToken)
            .ConfigureAwait(false);
        if (request.PinnedCanonicalSource is not null
            && !policy.Definition.SourceVendor.Equals(
                "OrchestraMSP", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Pinned canonical sources are restricted to OrchestraMSP policies.");
        var sourceDefinition = await RequireCurrentConnectionAsync(
            tenantId,
            policy.Definition.SourceConnectionId,
            expectedGeneration: null,
            cancellationToken).ConfigureAwait(false);
        var targetDefinition = await RequireCurrentConnectionAsync(
            tenantId,
            policy.Definition.TargetConnectionId,
            expectedGeneration: null,
            cancellationToken).ConfigureAwait(false);
        ValidatePolicyConnections(policy, sourceDefinition, targetDefinition);

        await using var sourceLease = await connections.AcquireAsync(
            tenantId,
            sourceDefinition.ConnectionId,
            sourceDefinition.Generation,
            cancellationToken).ConfigureAwait(false);
        await using var targetLease = await connections.AcquireAsync(
            tenantId,
            targetDefinition.ConnectionId,
            targetDefinition.Generation,
            cancellationToken).ConfigureAwait(false);
        var plannerRequest = ToPlannerRequest(request, policy);
        var plannerOutput = await planner.CreateSnapshotAsync(
            plannerRequest,
            sourceLease,
            targetLease,
            cancellationToken).ConfigureAwait(false);

        var activeExcludedSourceIds = await RecheckExclusionsAsync(
            policy, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(request.PlanLifetime);
        var manifest = manifestBuilder.Build(
            plannerOutput,
            policy,
            planId,
            actor,
            now,
            expiresAt,
            selection,
            activeExcludedSourceIds);
        await plans.InsertClaimedAsync(
            tenantId,
            manifest,
            requestSha256,
            ownerToken,
            cancellationToken).ConfigureAwait(false);
        var persisted = await plans.GetAsync(
            tenantId, manifest.Plan.PlanId, cancellationToken).ConfigureAwait(false);
        if (persisted is null
            || persisted.PlanDigestSha256 != manifest.Plan.PlanDigestSha256
            || persisted.ItemCount != manifest.Items.Count)
            throw new InvalidOperationException(
                "The committed durable plan could not be retrieved exactly after insertion.");
        return ToResult(persisted);
    }


    public async Task<DurablePlanInspectionPage> GetPageAsync(
        string tenantId,
        Guid planId,
        int page,
        int pageSize,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (page <= 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), pageSize, "Page size must be between 1 and 100.");
        var plan = await plans.GetAsync(tenantId, planId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DurablePlanNotFoundException(planId);
        var offset = checked((long)(page - 1) * pageSize);
        if (plan.ItemCount == 0 || offset >= plan.ItemCount)
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page is outside the plan.");
        if (plan.Status != EntitySyncDurablePlanStatus.Draft)
            throw new InvalidOperationException("Only a draft plan can be inspected.");
        var now = timeProvider.GetUtcNow();
        if (plan.ExpiresAt <= now) throw new DurablePlanExpiredException(planId);

        var persistedPage = await plans.GetPageAsync(
            tenantId, planId, page, pageSize, cancellationToken).ConfigureAwait(false);
        if (persistedPage.TotalItems != plan.ItemCount || persistedPage.Items.Count == 0)
            throw new InvalidOperationException("The durable plan page is inconsistent with its manifest.");
        var session = await plans.GetOrOpenInspectionAsync(
            tenantId,
            Guid.NewGuid(),
            plan.PlanId,
            plan.PlanDigestSha256,
            plan.SourceConnectionId,
            plan.SourceConnectionGeneration,
            plan.TargetConnectionId,
            plan.TargetConnectionGeneration,
            actor,
            now,
            cancellationToken).ConfigureAwait(false);
        var start = persistedPage.Items[0].ItemOrdinal;
        var end = persistedPage.Items[^1].ItemOrdinal;
        if (session.Status == EntitySyncInspectionStatus.Open)
        {
            var rangeId = StableGuid(EntitySyncCanonicalDigest.Compute(new
            {
                session.InspectionId,
                Start = start,
                End = end
            }));
            await plans.RecordInspectionRangeAsync(
                tenantId,
                session.InspectionId,
                rangeId,
                start,
                end,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        var ranges = await plans.ListInspectionRangesAsync(
            tenantId, session.InspectionId, cancellationToken).ConfigureAwait(false);
        var coverage = MergeCoverage(ranges, plan.ItemCount);
        var complete = IsExactCoverage(coverage, plan.ItemCount);
        if (complete && session.Status == EntitySyncInspectionStatus.Open)
        {
            session = await plans.CompleteInspectionAsync(
                tenantId,
                session.InspectionId,
                plan.PlanId,
                plan.PlanDigestSha256,
                plan.SourceConnectionId,
                plan.SourceConnectionGeneration,
                plan.TargetConnectionId,
                plan.TargetConnectionGeneration,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        return new DurablePlanInspectionPage(
            ToResult(plan),
            page,
            pageSize,
            persistedPage.Items,
            session.InspectionId,
            coverage,
            coverage.Sum(range => range.EndExclusive - range.StartInclusive),
            session.Status == EntitySyncInspectionStatus.Completed);
    }

    public Task<DurablePlanApprovalResult> ApproveAsync(
        string tenantId,
        Guid planId,
        string digest,
        EntitySyncActor actor,
        CancellationToken cancellationToken) =>
        ApproveCoreAsync(tenantId, planId, digest, actor, null, cancellationToken);

    public Task<EntitySyncDurablePlan?> GetControlPlanAsync(
        string tenantId,
        Guid planId,
        CancellationToken cancellationToken) =>
        plans.GetAsync(RequireTenant(tenantId), planId, cancellationToken);

    public async Task<DurablePlanApprovalResult?> RecoverControlApprovalAsync(
        string tenantId,
        Guid planId,
        string digest,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        var approval = await plans.GetApprovalAsync(
            RequireTenant(tenantId), approvalId, cancellationToken).ConfigureAwait(false);
        if (approval is null) return null;
        if (approval.PlanId != planId
            || approval.PlanDigestSha256 != new EntitySyncSha256(digest))
            throw new DurablePlanApprovalConflictException(planId);
        return ToApprovalResult(approval);
    }

    public Task<DurablePlanApprovalResult> ApproveControlAsync(
        string tenantId,
        Guid planId,
        string digest,
        EntitySyncActor actor,
        Guid approvalId,
        CancellationToken cancellationToken) =>
        ApproveCoreAsync(tenantId, planId, digest, actor, approvalId, cancellationToken);

    private async Task<DurablePlanApprovalResult> ApproveCoreAsync(
        string tenantId,
        Guid planId,
        string digest,
        EntitySyncActor actor,
        Guid? requestedApprovalId,
        CancellationToken cancellationToken)
    {
        tenantId = RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        var requestedDigest = new EntitySyncSha256(digest);
        if (requestedApprovalId is not null)
        {
            var existing = await plans.GetApprovalAsync(
                tenantId, requestedApprovalId.Value, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.PlanId != planId
                    || existing.PlanDigestSha256 != requestedDigest)
                    throw new DurablePlanApprovalConflictException(planId);
                return ToApprovalResult(existing);
            }
        }
        var plan = await plans.GetAsync(tenantId, planId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DurablePlanNotFoundException(planId);
        if (plan.PlanDigestSha256 != requestedDigest)
            throw new DurablePlanDigestMismatchException(planId);
        var now = timeProvider.GetUtcNow();
        if (plan.ExpiresAt <= now) throw new DurablePlanExpiredException(planId);
        if (plan.Status != EntitySyncDurablePlanStatus.Draft)
            throw new DurablePlanApprovalConflictException(planId);
        var inspection = await plans.FindInspectionAsync(
            tenantId, planId, requestedDigest, actor, cancellationToken)
            .ConfigureAwait(false);
        if (inspection is null
            || inspection.Status != EntitySyncInspectionStatus.Completed
            || inspection.CompletedAt is null)
            throw new PlanInspectionIncompleteException(planId);

        var policy = await policies.GetLatestAsync(tenantId, plan.PolicyId, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null
            || !policy.Enabled
            || policy.Version != plan.PolicyVersion
            || policy.DefinitionSha256 != plan.PolicyDefinitionSha256)
            throw new DurablePlanPolicyChangedException(planId);
        await RequireCurrentConnectionAsync(
            tenantId,
            plan.SourceConnectionId,
            plan.SourceConnectionGeneration,
            cancellationToken).ConfigureAwait(false);
        await RequireCurrentConnectionAsync(
            tenantId,
            plan.TargetConnectionId,
            plan.TargetConnectionGeneration,
            cancellationToken).ConfigureAwait(false);

        var approvalId = requestedApprovalId ?? Guid.NewGuid();
        var auditValuesElement = JsonSerializer.SerializeToElement(new
        {
            PlanId = plan.PlanId,
            Digest = plan.PlanDigestSha256.Value,
            InspectionId = inspection.InspectionId,
            plan.PolicyId,
            plan.PolicyVersion,
            plan.SourceConnectionId,
            plan.SourceConnectionGeneration,
            plan.TargetConnectionId,
            plan.TargetConnectionGeneration
        });
        var auditValues = new EntitySyncJsonValue(auditValuesElement.GetRawText());
        var audit = new EntitySyncAuditEvent(
            tenantId,
            Guid.NewGuid(),
            now,
            "SyncPlanApproved",
            actor,
            null,
            null,
            planId,
            null,
            approvalId.ToString("N"),
            auditValues,
            EntitySyncCanonicalDigest.Compute(auditValuesElement),
            null,
            null);
        EntitySyncApproval approval;
        try
        {
            approval = await plans.ApproveInspectionAsync(
                tenantId,
                approvalId,
                inspection.InspectionId,
                planId,
                requestedDigest,
                plan.SourceConnectionId,
                plan.SourceConnectionGeneration,
                plan.TargetConnectionId,
                plan.TargetConnectionGeneration,
                actor,
                now,
                plan.ExpiresAt,
                audit,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new DurablePlanApprovalConflictException(planId, exception);
        }
        return ToApprovalResult(approval);
    }

    private static DurablePlanApprovalResult ToApprovalResult(EntitySyncApproval approval) =>
        new(
            approval.TenantId,
            approval.PlanId,
            approval.ApprovalId,
            approval.InspectionId,
            approval.PlanDigestSha256.Value,
            approval.ApprovedAt,
            approval.ExpiresAt);

    private async Task<EntitySyncPolicy> ResolveCurrentPolicyAsync(
        string tenantId,
        Guid policyId,
        int? version,
        CancellationToken cancellationToken)
    {
        if (policyId == Guid.Empty) throw new ArgumentException("Policy ID is required.", nameof(policyId));
        EntitySyncPolicy? policy;
        if (version is null)
        {
            policy = await policies.GetLatestAsync(tenantId, policyId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
            policy = await policies.GetAsync(tenantId, policyId, version.Value, cancellationToken)
                .ConfigureAwait(false);
            var latest = await policies.GetLatestAsync(tenantId, policyId, cancellationToken)
                .ConfigureAwait(false);
            if (latest is not null && policy is not null && latest.Version != policy.Version)
                throw new InvalidOperationException("The requested policy version is no longer current.");
        }
        if (policy is null || !policy.Enabled)
            throw new InvalidOperationException("The exact enabled policy was not available.");
        return policy;
    }

    private async Task<EntitySyncConnectionDefinition> RequireCurrentConnectionAsync(
        string tenantId,
        string connectionId,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        var definition = await connectionDefinitions.GetAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || !definition.Enabled)
            throw new InvalidOperationException(
                $"Enabled connection '{connectionId}' was not available.");
        if (expectedGeneration is not null && definition.Generation != expectedGeneration)
            throw new DurablePlanConnectionChangedException(connectionId);
        return definition;
    }

    private async Task<IReadOnlySet<string>> RecheckExclusionsAsync(
        EntitySyncPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!policy.Definition.CreateMissing)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var route = EntityExclusionRoute.Create(
            policy.TenantId,
            policy.Definition.SourceVendor,
            policy.Definition.SourceConnectionId,
            policy.Definition.SourceEntityType,
            policy.Definition.TargetVendor,
            policy.Definition.TargetConnectionId,
            policy.Definition.TargetEntityType);
        try
        {
            var active = await exclusions.ListActiveAsync(route, cancellationToken)
                .ConfigureAwait(false);
            return active.Select(exclusion => exclusion.SourceEntityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EntityExclusionUnavailableException(
                "Permanent exclusions could not be rechecked; durable planning is blocked.",
                exception);
        }
    }

    private static CreateEntitySyncPlanRequest ToPlannerRequest(
        CreateDurablePlanRequest request,
        EntitySyncPolicy policy) =>
        new()
        {
            TenantId = policy.TenantId,
            SourceVendor = policy.Definition.SourceVendor,
            SourceConnectionId = policy.Definition.SourceConnectionId,
            TargetVendor = policy.Definition.TargetVendor,
            TargetConnectionId = policy.Definition.TargetConnectionId,
            SourceEntityType = policy.Definition.SourceEntityType,
            SourceSearch = request.SourceSearch,
            SourceCount = request.SourceCount,
            SourceEntityId = request.SourceEntityId,
            PinnedCanonicalSource = request.PinnedCanonicalSource,
            TargetEntityType = policy.Definition.TargetEntityType,
            CreateMissing = policy.Definition.CreateMissing,
            IncludeInactive = policy.Definition.IncludeInactive,
            AutoLinkScore = policy.Definition.AutoLinkScore,
            ReviewScore = policy.Definition.ReviewScore,
            SourceExternalIdName = policy.Definition.SourceExternalIdName,
            TargetCustomFieldName = policy.Definition.TargetCustomFieldName,
            UpdatePolicy = policy.Definition.UpdatePolicy,
            ChangeStateScope = policy.Definition.UpdatePolicy
                == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly
                ? EntitySyncCanonicalDigest.Compute(new
                {
                    policy.TenantId,
                    policy.RouteScope,
                    policy.PolicyId,
                    policy.Version
                }).Value
                : null
        };

    private static void ValidatePolicyConnections(
        EntitySyncPolicy policy,
        EntitySyncConnectionDefinition source,
        EntitySyncConnectionDefinition target)
    {
        if (!source.ConnectionId.Equals(policy.Definition.SourceConnectionId, StringComparison.Ordinal)
            || !source.Vendor.Equals(policy.Definition.SourceVendor, StringComparison.OrdinalIgnoreCase)
            || !target.ConnectionId.Equals(policy.Definition.TargetConnectionId, StringComparison.Ordinal)
            || !target.Vendor.Equals(policy.Definition.TargetVendor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The current connection definitions do not match the immutable policy.");
    }

    private static IReadOnlyList<DurableInspectionRange> MergeCoverage(
        IEnumerable<EntitySyncInspectionRange> ranges,
        int itemCount)
    {
        var merged = new List<DurableInspectionRange>();
        foreach (var range in ranges.OrderBy(range => range.RangeStart)
                     .ThenBy(range => range.RangeEnd))
        {
            if (range.RangeStart < 0 || range.RangeEnd >= itemCount)
                throw new InvalidOperationException("Stored inspection coverage is outside the manifest.");
            var next = new DurableInspectionRange(range.RangeStart, checked(range.RangeEnd + 1));
            if (merged.Count == 0 || next.StartInclusive > merged[^1].EndExclusive)
            {
                merged.Add(next);
                continue;
            }
            if (next.EndExclusive > merged[^1].EndExclusive)
                merged[^1] = merged[^1] with { EndExclusive = next.EndExclusive };
        }
        return merged;
    }

    private static bool IsExactCoverage(
        IReadOnlyList<DurableInspectionRange> ranges,
        int itemCount) =>
        ranges.Count == 1
        && ranges[0].StartInclusive == 0
        && ranges[0].EndExclusive == itemCount;

    private static Guid CreatePlanId(string tenantId, string idempotencyKey) =>
        StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            Namespace = "entitysync-durable-plan-idempotency-v1",
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey
        }));

    private static EntitySyncSha256 ComputeCreateRequestSha256(
        CreateDurablePlanRequest request,
        string tenantId,
        string idempotencyKey,
        EntitySyncSelectionBounds selection,
        EntitySyncActor actor) =>
        EntitySyncCanonicalDigest.Compute(new
        {
            SchemaVersion = 1,
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey,
            request.PolicyId,
            PolicyVersionSpecified = request.PolicyVersion.HasValue,
            request.PolicyVersion,
            selection.SourceSearch,
            selection.SourceCount,
            selection.SourceEntityId,
            PinnedCanonicalVersion = request.PinnedCanonicalSource?.CanonicalVersion,
            PinnedCanonicalEntitySha256 = request.PinnedCanonicalSource is null
                ? null
                : EntitySyncCanonicalDigest.Compute(request.PinnedCanonicalSource.Entity).Value,
            PlanLifetimeTicks = request.PlanLifetime.Ticks,
            CreatedBy = actor.ActorId
        });

    private static DurablePlanResult ToResult(EntitySyncDurablePlan plan) =>
        new(
            plan.TenantId,
            plan.PlanId,
            plan.PlanDigestSha256.Value,
            plan.ItemCount,
            plan.PolicyId,
            plan.PolicyVersion,
            plan.SourceConnectionGeneration,
            plan.TargetConnectionGeneration,
            plan.CreatedAt,
            plan.ExpiresAt);

    private static Guid StableGuid(EntitySyncSha256 digest)
    {
        var bytes = Convert.FromHexString(digest.Value);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string RequireTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        return tenantId.Trim();
    }

    private static void ValidateCreateRequest(CreateDurablePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(request.TenantId);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Trim().Length > 256)
            throw new ArgumentException(
                "A non-secret idempotency key of at most 256 characters is required.",
                nameof(request.IdempotencyKey));
        if (request.PolicyId == Guid.Empty)
            throw new ArgumentException("Policy ID is required.", nameof(request));
        if (request.PolicyVersion is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.PolicyVersion));
        if (request.PinnedCanonicalSource is not null)
        {
            if (request.SourceEntityId is null
                || request.PinnedCanonicalSource.CanonicalEntityId.ToString("D")
                    != request.SourceEntityId.Trim())
                throw new ArgumentException(
                    "Pinned canonical source must match SourceEntityId.", nameof(request));
            if (request.SourceSearch is not null || request.SourceCount is not null)
                throw new ArgumentException(
                    "Pinned canonical work cannot combine bounded search inputs.", nameof(request));
        }
        if (request.SourceCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.SourceCount));
        if (request.PlanLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.PlanLifetime));
    }
}

public sealed record DurablePlanCreationOptions(
    TimeSpan LeaseDuration,
    TimeSpan RenewalInterval,
    TimeSpan PollInterval)
{
    public static DurablePlanCreationOptions Default { get; } =
        new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(25));

    internal static DurablePlanCreationOptions Validate(DurablePlanCreationOptions options)
    {
        if (options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Lease duration must be positive.");
        if (options.RenewalInterval <= TimeSpan.Zero
            || options.RenewalInterval >= options.LeaseDuration)
            throw new ArgumentOutOfRangeException(
                nameof(options), "Renewal interval must be positive and shorter than the lease.");
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Poll interval must be positive.");
        return options;
    }
}

public sealed class DurablePlanNotFoundException(Guid planId)
    : KeyNotFoundException($"Durable plan '{planId}' was not found.");

public sealed class DurablePlanExpiredException(Guid planId)
    : InvalidOperationException($"Durable plan '{planId}' has expired.");

public sealed class DurablePlanDigestMismatchException(Guid planId)
    : InvalidOperationException($"Durable plan '{planId}' does not match the requested digest.");

public sealed class PlanInspectionIncompleteException(Guid planId)
    : InvalidOperationException($"Durable plan '{planId}' has not been inspected completely by this actor.");

public sealed class DurablePlanPolicyChangedException(Guid planId)
    : InvalidOperationException($"Durable plan '{planId}' policy is no longer current and enabled.");

public sealed class DurablePlanConnectionChangedException(string connectionId)
    : InvalidOperationException($"Connection '{connectionId}' no longer matches the durable plan generation.");

public sealed class DurablePlanIdempotencyConflictException(Guid planId)
    : InvalidOperationException(
        $"Durable plan idempotency identity '{planId}' is already bound to a different request.");

public sealed class DurablePlanCreationConflictException : InvalidOperationException
{
    public DurablePlanCreationConflictException(Guid planId)
        : base($"Durable plan creation '{planId}' lost exact claim ownership.")
    {
    }

    public DurablePlanCreationConflictException(Guid planId, Exception innerException)
        : base($"Durable plan creation '{planId}' lost exact claim ownership.", innerException)
    {
    }
}

public sealed class DurablePlanApprovalConflictException : InvalidOperationException
{
    public DurablePlanApprovalConflictException(Guid planId)
        : base($"Durable plan '{planId}' cannot be approved from its current state.")
    {
    }

    public DurablePlanApprovalConflictException(Guid planId, Exception innerException)
        : base($"Durable plan '{planId}' lost an approval race.", innerException)
    {
    }
}
