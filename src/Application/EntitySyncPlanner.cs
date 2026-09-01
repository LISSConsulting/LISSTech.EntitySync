using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Planning;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncPlanner(
    IConnectionRuntimeFactory connections,
    IEntitySyncPlanRepository plans,
    IEntityExclusionRepository exclusions,
    IEntityMatcher matcher,
    IEntityMapper mapper,
    IEntitySyncChangeStateRepository changeStates)
{
    private const int MaxEntitiesPerPlanSide = 5000;

    public async Task<EntitySyncPlan> CreateAsync(
        CreateEntitySyncPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var sourceVendor = EntitySyncVendors.Normalize(request.SourceVendor);
        var targetVendor = EntitySyncVendors.Normalize(request.TargetVendor);
        ValidateWorkflow(sourceVendor, targetVendor);
        await using var sourceLease = await connections.AcquireCurrentAsync(
            request.TenantId,
            sourceVendor,
            request.SourceConnectionId,
            cancellationToken).ConfigureAwait(false);
        await using var targetLease = await connections.AcquireCurrentAsync(
            request.TenantId,
            targetVendor,
            request.TargetConnectionId,
            cancellationToken).ConfigureAwait(false);
        var plan = await CreateSnapshotAsync(
            request,
            sourceLease,
            targetLease,
            cancellationToken).ConfigureAwait(false);
        plans.Add(plan);
        return plan;
    }

    public async Task<EntitySyncPlan> CreateSnapshotAsync(
        CreateEntitySyncPlanRequest request,
        IConnectionRuntimeLease sourceLease,
        IConnectionRuntimeLease targetLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceLease);
        ArgumentNullException.ThrowIfNull(targetLease);
        Validate(request);
        var sourceVendor = EntitySyncVendors.Normalize(request.SourceVendor);
        var targetVendor = EntitySyncVendors.Normalize(request.TargetVendor);
        ValidateWorkflow(sourceVendor, targetVendor);
        var sourceConnection = sourceLease.Definition;
        var targetConnection = targetLease.Definition;
        ValidatePinnedLease(
            request, sourceVendor, request.SourceConnectionId, sourceConnection, nameof(sourceLease));
        ValidatePinnedLease(
            request, targetVendor, request.TargetConnectionId, targetConnection, nameof(targetLease));
        var sourceType = request.SourceEntityType ?? DefaultEntityType(sourceVendor);
        var targetType = request.TargetEntityType ?? DefaultEntityType(targetVendor);
        var customFieldName = request.TargetCustomFieldName ?? DefaultCustomFieldName(sourceVendor, targetVendor);

        IReadOnlyList<ExternalEntity> sources;
        if (request.PinnedCanonicalSource is not null)
        {
            if (!sourceVendor.Equals("OrchestraMSP", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Pinned canonical sources are restricted to OrchestraMSP control work.");
            var pinned = request.PinnedCanonicalSource;
            var expectedSourceId = request.SourceEntityId?.Trim();
            if (expectedSourceId is null
                || pinned.CanonicalEntityId.ToString("D") != expectedSourceId
                || !pinned.Entity.Id.Equals(expectedSourceId, StringComparison.OrdinalIgnoreCase)
                || !pinned.Entity.EntityType.Equals(sourceType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Pinned canonical source identity does not match the durable selection.");
            sources = [pinned.Entity];
        }
        else
        {
            var sourceQuery = new EntityQuery
            {
                EntityType = sourceType,
                Search = request.SourceSearch?.Trim(),
                IncludeInactive = request.IncludeInactive,
                Count = request.SourceCount ?? MaxEntitiesPerPlanSide + 1
            };
            sources = await ReadEntitiesAsync(
                sourceLease.Adapter, sourceQuery, cancellationToken).ConfigureAwait(false);
            if (request.SourceEntityId is not null)
            {
                var expectedSourceId = request.SourceEntityId.Trim();
                sources = sources
                    .Where(source => source.Id.Equals(expectedSourceId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sources.Count != 1)
                    throw new ArgumentException(
                        $"Source entity ID '{expectedSourceId}' was not returned exactly once by the bounded source query. Adjust sourceSearch/sourceCount and retry.",
                        nameof(request));
            }
        }
        var targetQuery = new EntityQuery
        {
            EntityType = targetType,
            IncludeInactive = true,
            Count = MaxEntitiesPerPlanSide + 1
        };
        if (targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            targetQuery.RequiredCustomFieldName = customFieldName;
        var targets = await ReadEntitiesAsync(
            targetLease.Adapter, targetQuery, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, EntityExclusion> exclusionsBySourceId;
        if (request.CreateMissing)
        {
            var exclusionRoute = EntityExclusionRoute.Create(
                request.TenantId,
                sourceVendor,
                sourceConnection.ConnectionId,
                sourceType,
                targetVendor,
                targetConnection.ConnectionId,
                targetType);
            try
            {
                var activeExclusions = await exclusions.ListActiveAsync(exclusionRoute, cancellationToken).ConfigureAwait(false);
                exclusionsBySourceId = activeExclusions.ToDictionary(
                    exclusion => exclusion.SourceEntityId,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new EntityExclusionUnavailableException(
                    "Permanent exclusions could not be obtained; create-missing planning is blocked.",
                    ex);
            }
        }
        else
        {
            exclusionsBySourceId = new Dictionary<string, EntityExclusion>(StringComparer.OrdinalIgnoreCase);
        }
        if (sources.Count > MaxEntitiesPerPlanSide || targets.Count > MaxEntitiesPerPlanSide)
            throw new InvalidOperationException($"A plan is limited to {MaxEntitiesPerPlanSide} source and target entities. Narrow the synchronization scope.");

        var customerLinks = HaloNCentralPlanLinks.IsCustomerPlan(sourceVendor, sourceType, targetVendor, targetType, sourceLease.Adapter);
        var siteLinks = HaloNCentralPlanLinks.IsSitePlan(sourceVendor, sourceType, targetVendor, targetType, sourceLease.Adapter);
        if (customerLinks || siteLinks)
        {
            await HaloNCentralPlanLinks.ApplyAsync(sources, targets, sourceLease.Adapter, siteLinks, cancellationToken).ConfigureAwait(false);
        }

        var externalIdName = request.SourceExternalIdName
            ?? (siteLinks ? "NCentralSiteId" : customerLinks ? "NCentralCustomerId" : DefaultExternalIdName(sourceVendor));
        var options = new MatchOptions
        {
            SourceExternalIdName = externalIdName,
            TargetExternalIdName = externalIdName,
            TargetCustomFieldName = customFieldName,
            CreateMissing = request.CreateMissing,
            AutoLinkScore = request.AutoLinkScore,
            ReviewScore = request.ReviewScore
        };

        var changedOnly = request.UpdatePolicy == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly;
        IReadOnlyDictionary<string, EntitySyncChangeState>? storedChangeStates = null;
        if (changedOnly)
        {
            var route = EntitySyncChangeStateRoute.Create(
                request.TenantId,
                request.ChangeStateScope!,
                sourceVendor,
                sourceConnection.ConnectionId,
                sourceType,
                targetVendor,
                targetConnection.ConnectionId,
                targetType);
            storedChangeStates = await changeStates
                .GetBySourceIdsAsync(route, sources.Select(source => source.Id).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = new EntitySyncPlan
        {
            TenantId = request.TenantId.Trim(),
            SourceVendor = sourceVendor,
            SourceEntityType = sourceType,
            TargetVendor = targetVendor,
            TargetEntityType = targetType,
            TargetCandidates = targets.ToList(),
            Execution = new EntitySyncPlanExecution
            {
                SourceConnectionId = sourceConnection.ConnectionId,
                SourceConnectionGeneration = sourceConnection.Generation,
                TargetConnectionId = targetConnection.ConnectionId,
                TargetConnectionGeneration = targetConnection.Generation,
                MatchOptions = options,
                UpdatePolicy = request.UpdatePolicy,
                ChangeStateScope = request.ChangeStateScope
            }
        };

        var index = matcher.CreateIndex(targets, options);
        foreach (var source in sources)
        {
            if (exclusionsBySourceId.TryGetValue(source.Id, out var exclusion))
            {
                plan.Items.Add(new EntitySyncPlanItem
                {
                    Source = source,
                    Action = "None",
                    MatchType = "PersistentExclusion",
                    Status = "Excluded",
                    Reasons = [$"Permanently excluded: {exclusion.Reason}"]
                });
                continue;
            }

            var item = CreateItem(source, index.FindMatches(source), options, customerLinks || siteLinks);
            if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
                && EntitySyncVendors.IsOrchestraMSP(targetVendor)
                && (targetType.Equals("Site", StringComparison.OrdinalIgnoreCase)
                    || targetType.Equals(
                        "Address", StringComparison.OrdinalIgnoreCase)))
                await ResolveCreateParentAsync(
                    item, targetLease.Adapter, cancellationToken)
                    .ConfigureAwait(false);
            if (changedOnly) ApplyChangedOnlyPolicy(item, options, storedChangeStates!);
            plan.Items.Add(item);
        }
        return plan;
    }

    private static EntitySyncPlanItem CreateItem(ExternalEntity source, IReadOnlyList<EntityMatchCandidate> candidates, MatchOptions options, bool requiresAuthoritativeTarget)
    {
        if (source.CustomFields.TryGetValue("HaloNCentralIntegrationConflict", out var conflict) && !string.IsNullOrWhiteSpace(conflict))
        {
            return new EntitySyncPlanItem { Source = source, Action = "Review", MatchType = "IntegrationLinkConflict", Reasons = [conflict] };
        }

        var item = new EntitySyncPlanItem { Source = source };
        var best = candidates.FirstOrDefault();
        var authoritativeTargetId = source.GetExternalId(options.SourceExternalIdName);
        var tied = best != null && candidates.Skip(1).Any(candidate => candidate.Score == best.Score);
        if (tied)
        {
            item.Action = "Review";
            item.MatchType = "Ambiguous";
            item.Score = best!.Score;
            item.Reasons.Add("Multiple target candidates have the same highest score.");
            return item;
        }

        if (best == null)
        {
            if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId))
            {
                item.Action = "Review";
                item.MatchType = "IntegrationLinkTargetMissing";
                item.Reasons.Add($"The authoritative integration target {authoritativeTargetId} was not found in the target set.");
            }
            else
            {
                item.Action = options.CreateMissing ? "Create" : "Review";
                item.MatchType = "NoMatch";
                item.Reasons.Add("No target candidate found.");
            }
            return item;
        }

        item.MatchType = best.MatchType;
        item.Score = best.Score;
        item.Reasons.AddRange(best.Reasons);
        if (best.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase))
        {
            item.Target = best.Target;
            item.Action = "Update";
        }
        else if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId))
        {
            item.Action = "Review";
            item.MatchType = "IntegrationLinkTargetMissing";
        }
        else if (best.Score < options.ReviewScore)
        {
            item.Action = options.CreateMissing ? "Create" : "Review";
            item.Reasons.Add($"Best candidate '{best.Target.Name}' scored {best.Score}, below review threshold {options.ReviewScore}.");
        }
        else
        {
            item.Target = best.Target;
            item.Action = best.Score >= options.AutoLinkScore ? "Link" : "Review";
        }
        return item;
    }

    private static async Task ResolveCreateParentAsync(
        EntitySyncPlanItem item,
        IEntityAdapter targetAdapter,
        CancellationToken cancellationToken)
    {
        if (targetAdapter is not IEntityWriteParentResolver resolver)
        {
            HoldForParentReview(
                item, "ORCHESTRA_PARENT_RESOLVER_UNAVAILABLE");
            return;
        }
        var resolution = await resolver.ResolveWriteParentAsync(
            item.Source, cancellationToken).ConfigureAwait(false);
        if (resolution.Status == EntityWriteParentResolutionStatus.Resolved
            && resolution.Parent is not null)
        {
            item.ResolvedTargetParent = resolution.Parent;
            return;
        }
        HoldForParentReview(item, resolution.SafeCode);
    }

    private static void HoldForParentReview(
        EntitySyncPlanItem item,
        string safeCode)
    {
        item.Action = "Review";
        item.MatchType = "ParentLinkReview";
        item.Reasons.Add(
            $"Canonical parent resolution requires review ({safeCode}).");
    }

    private void ApplyChangedOnlyPolicy(
        EntitySyncPlanItem item,
        MatchOptions options,
        IReadOnlyDictionary<string, EntitySyncChangeState> storedChangeStates)
    {

        if (!item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase)
            || !item.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
            || item.Target is null)
        {
            item.Action = "None";
            item.Reasons.Add("Recurring changed-only sync permits persistently linked updates only.");
            return;
        }

        var write = mapper.MapUpdate(item.Source, item.Target, options);
        var hash = EntityWriteRequestDigest.Compute(write);
        item.DesiredStateHash = hash;
        item.DesiredStateHashVersion = EntityWriteRequestDigest.SchemaVersion;
        if (storedChangeStates.TryGetValue(item.Source.Id, out var state)
            && state.TargetEntityId.Equals(item.Target.Id, StringComparison.OrdinalIgnoreCase)
            && state.HashVersion == EntityWriteRequestDigest.SchemaVersion
            && state.PayloadHash.Equals(hash, StringComparison.Ordinal))
        {
            item.Action = "None";
            item.MatchType = "Unchanged";
            item.Reasons.Add("Mapped update payload matches the last successful synchronization.");
        }
    }

    private static void ValidatePinnedLease(
        CreateEntitySyncPlanRequest request,
        string expectedVendor,
        string? requestedConnectionId,
        EntitySyncConnectionDefinition definition,
        string parameterName)
    {
        if (!definition.Enabled
            || !definition.TenantId.Equals(request.TenantId.Trim(), StringComparison.Ordinal)
            || !definition.Vendor.Equals(expectedVendor, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The pinned connection lease does not match the requested tenant and vendor.",
                parameterName);

        if (requestedConnectionId is not null
            && !definition.ConnectionId.Equals(requestedConnectionId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException(
                "The pinned connection lease does not match the requested connection ID.",
                parameterName);
    }

    private static void Validate(CreateEntitySyncPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId)) throw new ArgumentException("Tenant ID is required.", nameof(request));
        if (request.ReviewScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Review score must be between 0 and 100.");
        if (request.AutoLinkScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Auto-link score must be between 0 and 100.");
        if (request.ReviewScore > request.AutoLinkScore) throw new ArgumentException("Review score cannot exceed auto-link score.", nameof(request));
        if (request.SourceSearch is not null)
        {
            if (string.IsNullOrWhiteSpace(request.SourceSearch)) throw new ArgumentException("Source search cannot be blank when supplied.", nameof(request));
            if (request.SourceSearch.Trim().Length > 512) throw new ArgumentException("Source search cannot exceed 512 characters.", nameof(request));
        }
        if (request.SourceCount is <= 0 or > MaxEntitiesPerPlanSide)
            throw new ArgumentOutOfRangeException(nameof(request), $"Source count must be between 1 and {MaxEntitiesPerPlanSide}.");
        if (request.SourceEntityId is not null)
        {
            if (string.IsNullOrWhiteSpace(request.SourceEntityId)) throw new ArgumentException("Source entity ID cannot be blank when supplied.", nameof(request));
            if (request.SourceEntityId.Trim().Length > 512) throw new ArgumentException("Source entity ID cannot exceed 512 characters.", nameof(request));
        }
        if (request.UpdatePolicy == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly
            && !IsValidChangeStateScope(request.ChangeStateScope))
            throw new ArgumentException(
                "Change-state scope must be a lowercase 64-character hexadecimal value.",
                nameof(request));
    }

    private static bool IsValidChangeStateScope(string? scope) =>
        scope is { Length: 64 }
        && scope.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateWorkflow(string sourceVendor, string targetVendor)
    {
        if (sourceVendor.Equals(targetVendor, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and target vendors must be different.");
        if (targetVendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NetSuite is read-only in the application executor and cannot be used as a plan target.");
        var requiresHaloWriteBack = sourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && (targetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase) || EntitySyncVendors.IsBillCom(targetVendor));
        if (requiresHaloWriteBack)
            throw new ArgumentException($"{sourceVendor} to {targetVendor} requires a source integration-link writeback that is not available through the application executor. Use the reviewed PowerShell execution workflow.");
    }

    private static string DefaultEntityType(string vendor) => vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) || EntitySyncVendors.IsBillCom(vendor) ? "Client" : "Customer";
    private static string DefaultExternalIdName(string vendor) => EntitySyncVendors.IsBillCom(vendor) ? "BillSpendClientId" : "NetSuiteInternalId";
    private static string DefaultCustomFieldName(string sourceVendor, string targetVendor) => EntitySyncVendors.IsBillCom(sourceVendor) && targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) ? "CFBillSpendClientID" : "CFNetSuiteCustomerID";
    private static async Task<IReadOnlyList<ExternalEntity>> ReadEntitiesAsync(
        IEntityAdapter adapter,
        EntityQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.GetEntitiesAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EntitySyncDependencyUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The entity adapter is unavailable.", exception);
        }
    }

}
