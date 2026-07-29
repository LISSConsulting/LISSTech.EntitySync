using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Planning;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using ModelContextProtocol.Server;

namespace LISSTech.EntitySync.Mcp;

[McpServerToolType]
public static class SyncTools
{
    [McpServerTool]
    [Description("Create an entity synchronization plan between a source and target vendor. Reads entities from both sides, performs fuzzy matching, and returns a reviewable plan. Does not write to any vendor. Supported sources: HaloPSA, NetSuite, NCentral, Bill.com. Supported targets: HaloPSA, NetSuite, NCentral, Bill.com.")]
    public static async Task<string> CreateSyncPlan(
        SyncSession session,
        [Description("Source vendor: HaloPSA, NetSuite, NCentral, or Bill.com")] string sourceVendor,
        [Description("Target vendor: HaloPSA, NetSuite, NCentral, or Bill.com")] string targetVendor,
        [Description("Source entity type (e.g. Client, Customer, Site). Defaults to the vendor's primary type.")] string? sourceEntityType = null,
        [Description("Target entity type. Defaults to the vendor's primary type.")] string? targetEntityType = null,
        [Description("Create missing target entities during apply")] bool createMissing = false,
        [Description("Include inactive source entities")] bool includeInactive = false,
        [Description("Auto-link score threshold (0-100, default 90)")] int autoLinkScore = 90,
        [Description("Review score threshold (0-100, default 70)")] int reviewScore = 70,
        [Description("Source external ID name for link matching. Auto-detected from vendor (e.g. BillSpendClientId for Bill.com, NetSuiteInternalId for NetSuite).")] string? sourceExternalIdName = null,
        [Description("Target custom field name for link matching (e.g. CFNetSuiteCustomerID, CFBillSpendClientID). Auto-detected when possible.")] string? targetCustomFieldName = null)
    {
        try
        {
            var normalizedSource = EntitySyncVendors.Normalize(sourceVendor);
            var normalizedTarget = EntitySyncVendors.Normalize(targetVendor);

            var sourceAdapter = ConnectionRegistry.Get(normalizedSource);
            var targetAdapter = ConnectionRegistry.Get(normalizedTarget);

            var srcType = sourceEntityType ?? DefaultEntityType(normalizedSource);
            var tgtType = targetEntityType ?? DefaultEntityType(normalizedTarget);

            var customFieldName = targetCustomFieldName ?? DefaultCustomFieldName(normalizedSource, normalizedTarget);

            var sourceQuery = new EntityQuery { EntityType = srcType, IncludeInactive = includeInactive };
            var targetQuery = new EntityQuery { EntityType = tgtType, IncludeInactive = true };

            if (normalizedTarget.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            {
                targetQuery.RequiredCustomFieldName = customFieldName;
            }

            var sources = await sourceAdapter.GetEntitiesAsync(sourceQuery, CancellationToken.None);
            var targets = await targetAdapter.GetEntitiesAsync(targetQuery, CancellationToken.None);

            var usingHaloNCentralLinks = HaloNCentralPlanLinks.IsCustomerPlan(normalizedSource, srcType, normalizedTarget, tgtType, sourceAdapter);
            var usingHaloNCentralSiteLinks = HaloNCentralPlanLinks.IsSitePlan(normalizedSource, srcType, normalizedTarget, tgtType, sourceAdapter);
            HaloNCentralLinkResult? resolvedLinks = null;
            if (usingHaloNCentralLinks || usingHaloNCentralSiteLinks)
            {
                resolvedLinks = await HaloNCentralPlanLinks.ApplyAsync(sources, targets, sourceAdapter, usingHaloNCentralSiteLinks, CancellationToken.None);
            }

            var extIdName = sourceExternalIdName
                ?? (usingHaloNCentralSiteLinks ? "NCentralSiteId"
                    : usingHaloNCentralLinks ? "NCentralCustomerId"
                    : DefaultExternalIdName(normalizedSource));
            var requiresAuthoritativeTarget = usingHaloNCentralLinks || usingHaloNCentralSiteLinks;

            var options = new MatchOptions
            {
                SourceExternalIdName = extIdName,
                TargetExternalIdName = extIdName,
                TargetCustomFieldName = customFieldName,
                CreateMissing = createMissing,
                AutoLinkScore = autoLinkScore,
                ReviewScore = reviewScore
            };

            var matcher = new WeightedEntityMatcher();
            var index = matcher.CreateIndex(targets, options);

            var plan = new EntitySyncPlan
            {
                SourceVendor = normalizedSource,
                SourceEntityType = srcType,
                TargetVendor = normalizedTarget,
                TargetEntityType = tgtType,
                TargetCandidates = targets.ToList()
            };

            foreach (var source in sources)
            {
                if (source.CustomFields.TryGetValue("HaloNCentralIntegrationConflict", out var conflict) && !string.IsNullOrWhiteSpace(conflict))
                {
                    var conflictItem = new EntitySyncPlanItem { Source = source, Action = "Review", MatchType = "IntegrationLinkConflict" };
                    conflictItem.Reasons.Add(conflict);
                    plan.Items.Add(conflictItem);
                    continue;
                }

                var candidates = index.FindMatches(source);
                var best = candidates.FirstOrDefault();
                var authoritativeTargetId = source.GetExternalId(extIdName);

                var item = new EntitySyncPlanItem { Source = source };

                if (best == null)
                {
                    if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId))
                    {
                        item.Action = "Review";
                        item.MatchType = "IntegrationLinkTargetMissing";
                        item.Reasons.Add($"HaloPSA N-central integration links this source to N-central target {authoritativeTargetId}, but that target was not found in the fetched N-central target set.");
                    }
                    else
                    {
                        item.Action = createMissing ? "Create" : "Review";
                        item.MatchType = "NoMatch";
                        item.Reasons.Add("No target candidate found");
                    }
                }
                else if (best.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase))
                {
                    item.Target = best.Target;
                    item.Action = "Update";
                    item.MatchType = best.MatchType;
                    item.Score = best.Score;
                    item.Reasons.AddRange(best.Reasons);
                }
                else if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId))
                {
                    item.Action = "Review";
                    item.MatchType = "IntegrationLinkTargetMissing";
                    item.Reasons.Add($"HaloPSA N-central integration links this source to N-central target {authoritativeTargetId}, but that target was not found in the fetched N-central target set.");
                }
                else if (best.Score < reviewScore)
                {
                    item.Action = createMissing ? "Create" : "Review";
                    item.MatchType = best.MatchType;
                    item.Score = best.Score;
                    item.Reasons.AddRange(best.Reasons);
                    item.Reasons.Add($"Best candidate '{best.Target.Name}' scored {best.Score}, below review threshold {reviewScore}.");
                }
                else
                {
                    item.Target = best.Target;
                    item.Action = best.Score >= autoLinkScore ? "Link" : "Review";
                    item.MatchType = best.MatchType;
                    item.Score = best.Score;
                    item.Reasons.AddRange(best.Reasons);
                }

                plan.Items.Add(item);
            }

            var planId = Guid.NewGuid().ToString("N")[..8];
            session.StorePlan(planId, plan);

            var summary = new
            {
                success = true,
                planId,
                sourceVendor = normalizedSource,
                sourceEntityType = srcType,
                targetVendor = normalizedTarget,
                targetEntityType = tgtType,
                sourceCount = sources.Count,
                targetCount = targets.Count,
                authoritativeLinks = resolvedLinks,
                actions = plan.Items.GroupBy(i => i.Action).ToDictionary(g => g.Key, g => g.Count()),
                itemCount = plan.Items.Count,
                itemsTruncated = plan.Items.Count > 25,
                items = plan.Items.Take(25).Select(i => new
                {
                    action = i.Action,
                    matchType = i.MatchType,
                    score = i.Score,
                    source = i.Source.Name,
                    target = i.Target?.Name,
                    reasons = i.Reasons
                })
            };

            return JsonSerializer.Serialize(summary, SyncJsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Apply a synchronization plan to write create/link/update actions to the target vendor. Requires -Apply. Supports WhatIf dry-run by passing apply=false.")]
    public static async Task<string> ApplySyncPlan(
        SyncSession session,
        [Description("Plan ID returned from CreateSyncPlan")] string planId,
        [Description("Set to true to actually write changes. False (default) is a dry-run.")] bool apply = false)
    {
        try
        {
            var plan = session.GetPlan(planId);
            if (plan == null)
                return JsonSerializer.Serialize(new { success = false, error = $"Plan '{planId}' not found. Create one with CreateSyncPlan first." });

            var mapper = new DefaultEntityMapper();
            var extIdName = DefaultExternalIdName(plan.SourceVendor);
            var customFieldName = DefaultCustomFieldName(plan.SourceVendor, plan.TargetVendor);
            var options = new MatchOptions
            {
                SourceExternalIdName = extIdName,
                TargetCustomFieldName = customFieldName
            };

            var targetAdapter = ConnectionRegistry.Get(plan.TargetVendor);
            var results = new List<object>();

            foreach (var item in plan.Items)
            {
                if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase) || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new { item.Action, source = item.Source.Name, success = false, message = "Skipped: requires review." });
                    continue;
                }

                if (!apply)
                {
                    results.Add(new { item.Action, source = item.Source.Name, success = true, message = "Dry-run: pass apply=true to write." });
                    continue;
                }

                try
                {
                    EntityWriteResult result;
                    if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                    {
                        var request = mapper.MapCreate(item.Source, plan.TargetVendor, plan.TargetEntityType, options);
                        if (plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                        result = await targetAdapter.CreateEntityAsync(request, CancellationToken.None);
                    }
                    else
                    {
                        var request = mapper.MapUpdate(item.Source, item.Target!, options);
                        if (plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                        result = await targetAdapter.UpdateEntityAsync(request, CancellationToken.None);
                    }
                    results.Add(new { item.Action, source = item.Source.Name, target = item.Target?.Name, result.Success, result.Id, result.Message });
                }
                catch (Exception ex)
                {
                    results.Add(new { item.Action, source = item.Source.Name, success = false, message = ex.Message });
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                planId,
                applied = apply,
                targetVendor = plan.TargetVendor,
                results
            }, SyncJsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    internal static readonly JsonSerializerOptions SyncJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static string DefaultEntityType(string vendor)
    {
        if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return "Client";
        if (EntitySyncVendors.IsBillCom(vendor)) return "Client";
        return "Customer";
    }

    private static string DefaultExternalIdName(string sourceVendor)
    {
        if (EntitySyncVendors.IsBillCom(sourceVendor)) return "BillSpendClientId";
        return "NetSuiteInternalId";
    }

    private static string DefaultCustomFieldName(string sourceVendor, string targetVendor)
    {
        if (EntitySyncVendors.IsBillCom(sourceVendor) && targetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
            return "CFBillSpendClientID";
        return "CFNetSuiteCustomerID";
    }
}
