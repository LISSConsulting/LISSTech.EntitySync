using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Planning;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncPlanner(
    IEntityConnectionRepository connections,
    IEntitySyncPlanRepository plans,
    IEntityExclusionRepository exclusions,
    IEntityMatcher matcher,
    IEntityMapper mapper,
    IEntitySyncChangeStateRepository changeStates,
    IEntityGraphRepository graph)
{
    private const int MaxEntitiesPerPlanSide = 5000;

    public async Task<EntitySyncPlan> CreateAsync(CreateEntitySyncPlanRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var sourceVendor = EntitySyncVendors.Normalize(request.SourceVendor);
        var targetVendor = EntitySyncVendors.Normalize(request.TargetVendor);
        ValidateWorkflow(sourceVendor, targetVendor);
        using var sourceLease = connections.Acquire(request.TenantId, sourceVendor, request.SourceConnectionId);
        using var targetLease = connections.Acquire(request.TenantId, targetVendor, request.TargetConnectionId);
        var sourceConnection = sourceLease.Connection;
        var targetConnection = targetLease.Connection;
        var sourceType = request.SourceEntityType
            ?? (sourceVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase) && EntitySyncVendors.IsAgentController(targetVendor)
                ? "CustomerScope"
                : DefaultEntityType(sourceVendor));
        var targetType = request.TargetEntityType ?? DefaultEntityType(targetVendor);
        var customFieldName = request.TargetCustomFieldName ?? DefaultCustomFieldName(sourceVendor, targetVendor);
        var authoritativeBillSnapshot = BillComPlanReconciliation.IsAuthoritativeRoute(sourceVendor, sourceType, targetVendor, targetType);
        var authoritativeAgentControllerSnapshot = EntitySyncVendors.IsAgentController(targetVendor);
        if (request.BootstrapExactNameLinks
            && (!authoritativeBillSnapshot
                || request.UpdatePolicy != EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly))
        {
            throw new ArgumentException(
                "Exact-name link bootstrap is restricted to changed-only HaloPSA-to-BILL.com plans.",
                nameof(request));
        }
        if (authoritativeAgentControllerSnapshot
            && (!sourceVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
                || !sourceType.Equals("CustomerScope", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("AgentController apply requires a complete N-central CustomerScope source snapshot.");
        }
        if (authoritativeAgentControllerSnapshot
            && (!string.IsNullOrWhiteSpace(request.SourceSearch)
                || request.SourceCount.HasValue
                || !string.IsNullOrWhiteSpace(request.SourceEntityId)))
        {
            throw new ArgumentException("AgentController authoritative planning cannot use sourceSearch, sourceCount, or sourceEntityId because the complete N-central customer-and-site snapshot is required.");
        }
        if (authoritativeBillSnapshot
            && (!string.IsNullOrWhiteSpace(request.SourceSearch)
                || request.SourceCount.HasValue
                || !string.IsNullOrWhiteSpace(request.SourceEntityId)))
        {
            throw new ArgumentException("BILL.com exact-list reconciliation cannot use sourceSearch, sourceCount, or sourceEntityId because the complete HaloPSA client list is required.");
        }



        var sourceQuery = new EntityQuery
        {
            EntityType = sourceType,
            Search = request.SourceSearch?.Trim(),
            IncludeInactive = request.IncludeInactive,
            Count = request.SourceCount ?? MaxEntitiesPerPlanSide + 1,
            FullObjects = authoritativeBillSnapshot,
            RequiredCustomFieldName = RequiredSourceCustomFieldName(sourceVendor, targetVendor, authoritativeBillSnapshot)
        };
        var targetQuery = new EntityQuery { EntityType = targetType, IncludeInactive = true, Count = MaxEntitiesPerPlanSide + 1 };
        if (targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) targetQuery.RequiredCustomFieldName = customFieldName;

        IReadOnlyList<ExternalEntity> sources;
        if (authoritativeAgentControllerSnapshot)
        {
            var customers = await sourceConnection.Adapter.GetEntitiesAsync(
                new EntityQuery
                {
                    EntityType = "Customer",
                    IncludeInactive = request.IncludeInactive,
                    Count = MaxEntitiesPerPlanSide + 1
                },
                cancellationToken).ConfigureAwait(false);
            var sites = await sourceConnection.Adapter.GetEntitiesAsync(
                new EntityQuery
                {
                    EntityType = "Site",
                    IncludeInactive = request.IncludeInactive,
                    Count = MaxEntitiesPerPlanSide + 1
                },
                cancellationToken).ConfigureAwait(false);
            sources = customers.Concat(sites).ToArray();
        }
        else
        {
            sources = await sourceConnection.Adapter.GetEntitiesAsync(sourceQuery, cancellationToken).ConfigureAwait(false);
        }
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
        var targets = await targetConnection.Adapter.GetEntitiesAsync(targetQuery, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, EntityExclusion> exclusionsBySourceId;
        if (request.CreateMissing)
        {
            var exclusionRoute = EntityExclusionRoute.Create(
                request.TenantId,
                sourceVendor,
                sourceConnection.Id,
                sourceType,
                targetVendor,
                targetConnection.Id,
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

        var customerLinks = HaloNCentralPlanLinks.IsCustomerPlan(sourceVendor, sourceType, targetVendor, targetType, sourceConnection.Adapter);
        var siteLinks = HaloNCentralPlanLinks.IsSitePlan(sourceVendor, sourceType, targetVendor, targetType, sourceConnection.Adapter);
        if (customerLinks || siteLinks)
        {
            await HaloNCentralPlanLinks.ApplyAsync(sources, targets, sourceConnection.Adapter, siteLinks, cancellationToken).ConfigureAwait(false);
        }

        var externalIdName = request.SourceExternalIdName
            ?? (siteLinks
                ? "NCentralSiteId"
                : customerLinks
                    ? "NCentralCustomerId"
                    : authoritativeBillSnapshot
                        ? EntitySyncIntegrationContracts.BillComClientExternalIdName
                        : DefaultExternalIdName(sourceVendor));
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
                sourceConnection.Id,
                sourceType,
                targetVendor,
                targetConnection.Id,
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
                SourceConnectionId = sourceConnection.Id,
                SourceConnectionGeneration = sourceConnection.Generation,
                TargetConnectionId = targetConnection.Id,
                TargetConnectionGeneration = targetConnection.Generation,
                MatchOptions = options,
                UpdatePolicy = request.UpdatePolicy,
                ChangeStateScope = request.ChangeStateScope
            }
        };
        await ObserveEntitiesAsync(
            plan.TenantId,
            sourceVendor,
            sourceConnection.Id,
            sourceType,
            sources,
            plan.CreatedAt,
            plan.Id,
            cancellationToken).ConfigureAwait(false);
        await ObserveEntitiesAsync(
            plan.TenantId,
            targetVendor,
            targetConnection.Id,
            targetType,
            targets,
            plan.CreatedAt,
            plan.Id,
            cancellationToken).ConfigureAwait(false);

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
            if (changedOnly) ApplyChangedOnlyPolicy(item, options, storedChangeStates!, request.BootstrapExactNameLinks);
            plan.Items.Add(item);
        }
        BillComPlanReconciliation.AddApprovedTargetOperations(plan);
        await graph.ObserveRelationshipsAsync(
            CreateRelationshipObservations(plan),
            cancellationToken).ConfigureAwait(false);
        plans.Add(plan);
        return plan;
    }

    private async Task ObserveEntitiesAsync(
        string tenantId,
        string vendor,
        string connectionId,
        string fallbackEntityType,
        IReadOnlyCollection<ExternalEntity> entities,
        DateTimeOffset observedAt,
        string planId,
        CancellationToken cancellationToken)
    {
        foreach (var group in entities.GroupBy(
                     entity => string.IsNullOrWhiteSpace(entity.EntityType)
                         ? fallbackEntityType
                         : entity.EntityType,
                     StringComparer.OrdinalIgnoreCase))
        {
            await graph.ObserveEntitiesAsync(
                new EntityGraphObservation(
                    new EntityGraphScope(tenantId, vendor, connectionId, group.Key),
                    group.ToArray(),
                    observedAt,
                    planId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyCollection<EntityGraphRelationshipObservation> CreateRelationshipObservations(
        EntitySyncPlan plan)
    {
        var observedAt = plan.CreatedAt;
        return plan.Items
            .Where(item => item.Target is not null)
            .Where(item => !item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase)
                && !item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
                && !item.Action.Equals("Delete", StringComparison.OrdinalIgnoreCase))
            .Select(item => new EntityGraphRelationshipObservation(
                new EntityGraphNodeKey(
                    plan.TenantId,
                    plan.SourceVendor,
                    plan.Execution.SourceConnectionId,
                    string.IsNullOrWhiteSpace(item.Source.EntityType) ? plan.SourceEntityType : item.Source.EntityType,
                    item.Source.Id),
                new EntityGraphNodeKey(
                    plan.TenantId,
                    plan.TargetVendor,
                    plan.Execution.TargetConnectionId,
                    string.IsNullOrWhiteSpace(item.Target!.EntityType) ? plan.TargetEntityType : item.Target.EntityType,
                    item.Target.Id),
                EntityGraphRelationshipTypes.EquivalentTo,
                item.Action.Equals("Link", StringComparison.OrdinalIgnoreCase)
                    ? EntityGraphRelationshipStatuses.Proposed
                    : EntityGraphRelationshipStatuses.Confirmed,
                item.MatchType,
                item.Score,
                item.Reasons.ToArray(),
                observedAt,
                plan.Id))
            .ToArray();
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

    private void ApplyChangedOnlyPolicy(
        EntitySyncPlanItem item,
        MatchOptions options,
        IReadOnlyDictionary<string, EntitySyncChangeState> storedChangeStates,
        bool bootstrapExactNameLinks)
    {
        if (bootstrapExactNameLinks && IsSafeExactNameBootstrap(item, options))
        {
            item.Action = "Link";
            item.MatchType = "BootstrapExactName";
            item.Reasons.Add("Unique active BILL.com value has the exact normalized HaloPSA client name; bootstrap its immutable ID.");
            SetDesiredState(item, options);
            return;
        }

        if (!item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase)
            || !item.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
            || item.Target is null)
        {
            item.Action = "None";
            item.Reasons.Add("Recurring changed-only sync permits persistently linked updates only.");
            return;
        }

        SetDesiredState(item, options);
        if (storedChangeStates.TryGetValue(item.Source.Id, out var state)
            && state.TargetEntityId.Equals(item.Target.Id, StringComparison.OrdinalIgnoreCase)
            && state.HashVersion == EntityWriteRequestDigest.SchemaVersion
            && state.PayloadHash.Equals(item.DesiredStateHash, StringComparison.Ordinal))
        {
            item.Action = "None";
            item.MatchType = "Unchanged";
            item.Reasons.Add("Mapped update payload matches the last successful synchronization.");
        }
    }

    private static bool IsSafeExactNameBootstrap(EntitySyncPlanItem item, MatchOptions options)
    {
        if (item.Target is null
            || item.Target.IsActive == false
            || item.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(item.Source.GetExternalId(options.SourceExternalIdName)))
        {
            return false;
        }

        var sourceName = EntityNormalizer.NormalizeName(item.Source.Name);
        return sourceName.Length > 0
            && sourceName.Equals(EntityNormalizer.NormalizeName(item.Target.Name), StringComparison.Ordinal);
    }

    private void SetDesiredState(EntitySyncPlanItem item, MatchOptions options)
    {
        var write = mapper.MapUpdate(item.Source, item.Target!, options);
        item.DesiredStateHash = EntityWriteRequestDigest.Compute(write);
        item.DesiredStateHashVersion = EntityWriteRequestDigest.SchemaVersion;
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
            throw new ArgumentException($"{targetVendor} is read-only and cannot be used as a plan target.");
        if (EntitySyncVendors.IsAgentController(targetVendor)
            && !sourceVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("AgentController authoritative synchronization requires NCentral as the source vendor.");
        }
    }

    private static string DefaultEntityType(string vendor) => vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) || EntitySyncVendors.IsBillCom(vendor) ? "Client" : "Customer";
    private static string DefaultExternalIdName(string vendor)
    {
        if (EntitySyncVendors.IsBillCom(vendor)) return EntitySyncIntegrationContracts.BillComClientExternalIdName;
        return EntitySyncVendors.IsSophosCentral(vendor)
            ? EntitySyncIntegrationContracts.SophosCentralTenantExternalIdName
            : "NetSuiteInternalId";
    }

    private static string? RequiredSourceCustomFieldName(
        string sourceVendor,
        string targetVendor,
        bool authoritativeBillSnapshot)
    {
        if (authoritativeBillSnapshot) return EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName;
        return sourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && EntitySyncVendors.IsSophosCentral(targetVendor)
                ? EntitySyncIntegrationContracts.SophosCentralHaloTenantCustomFieldName
                : null;
    }

    private static string DefaultCustomFieldName(string sourceVendor, string targetVendor)
    {
        if (targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            if (EntitySyncVendors.IsBillCom(sourceVendor)) return EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName;
            if (EntitySyncVendors.IsSophosCentral(sourceVendor)) return EntitySyncIntegrationContracts.SophosCentralHaloTenantCustomFieldName;
        }

        return "CFNetSuiteCustomerID";
    }
}
