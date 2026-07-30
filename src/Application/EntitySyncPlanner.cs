using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Planning;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncPlanner(IEntityConnectionRepository connections, IEntitySyncPlanRepository plans, IEntityMatcher matcher)
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
        var sourceType = request.SourceEntityType ?? DefaultEntityType(sourceVendor);
        var targetType = request.TargetEntityType ?? DefaultEntityType(targetVendor);
        var customFieldName = request.TargetCustomFieldName ?? DefaultCustomFieldName(sourceVendor, targetVendor);

        var sourceQuery = new EntityQuery { EntityType = sourceType, IncludeInactive = request.IncludeInactive, Count = MaxEntitiesPerPlanSide + 1 };
        var targetQuery = new EntityQuery { EntityType = targetType, IncludeInactive = true, Count = MaxEntitiesPerPlanSide + 1 };
        if (targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) targetQuery.RequiredCustomFieldName = customFieldName;

        var sources = await sourceConnection.Adapter.GetEntitiesAsync(sourceQuery, cancellationToken).ConfigureAwait(false);
        var targets = await targetConnection.Adapter.GetEntitiesAsync(targetQuery, cancellationToken).ConfigureAwait(false);
        if (sources.Count > MaxEntitiesPerPlanSide || targets.Count > MaxEntitiesPerPlanSide)
            throw new InvalidOperationException($"A plan is limited to {MaxEntitiesPerPlanSide} source and target entities. Narrow the synchronization scope.");

        var customerLinks = HaloNCentralPlanLinks.IsCustomerPlan(sourceVendor, sourceType, targetVendor, targetType, sourceConnection.Adapter);
        var siteLinks = HaloNCentralPlanLinks.IsSitePlan(sourceVendor, sourceType, targetVendor, targetType, sourceConnection.Adapter);
        if (customerLinks || siteLinks)
        {
            await HaloNCentralPlanLinks.ApplyAsync(sources, targets, sourceConnection.Adapter, siteLinks, cancellationToken).ConfigureAwait(false);
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
                MatchOptions = options
            }
        };

        var index = matcher.CreateIndex(targets, options);
        foreach (var source in sources) plan.Items.Add(CreateItem(source, index.FindMatches(source), options, customerLinks || siteLinks));
        plans.Add(plan);
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

    private static void Validate(CreateEntitySyncPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId)) throw new ArgumentException("Tenant ID is required.", nameof(request));
        if (request.ReviewScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Review score must be between 0 and 100.");
        if (request.AutoLinkScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Auto-link score must be between 0 and 100.");
        if (request.ReviewScore > request.AutoLinkScore) throw new ArgumentException("Review score cannot exceed auto-link score.", nameof(request));
    }

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
}
