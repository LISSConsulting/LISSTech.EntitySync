using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Planning;

public sealed record HaloNCentralLinkResult(int ClientLinks, int SiteLinks, int ParentLinks, int ExternalIdLinks);

public static class HaloNCentralPlanLinks
{
    public static bool IsCustomerPlan(string sourceVendor, string sourceEntityType, string targetVendor, string targetEntityType, IEntityAdapter sourceAdapter)
    {
        return sourceAdapter.Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && sourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && targetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            && targetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSitePlan(string sourceVendor, string sourceEntityType, string targetVendor, string targetEntityType, IEntityAdapter sourceAdapter)
    {
        return sourceAdapter.Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && sourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && targetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && targetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<HaloNCentralLinkResult> ApplyAsync(
        IReadOnlyList<ExternalEntity> sources,
        IReadOnlyList<ExternalEntity> targets,
        IEntityAdapter sourceAdapter,
        bool sitePlan,
        CancellationToken cancellationToken)
    {
        var lookups = await sourceAdapter.GetLookupsAsync(EntitySyncLookupTypes.NCentralIntegrationLink, cancellationToken).ConfigureAwait(false);
        return ApplyLookups(sources, targets, lookups, sitePlan);
    }

    public static HaloNCentralLinkResult ApplyLookups(
        IReadOnlyList<ExternalEntity> sources,
        IReadOnlyList<ExternalEntity> targets,
        IReadOnlyList<EntitySyncLookup> lookups,
        bool sitePlan)
    {
        var links = lookups.Select(ToIntegrationLink).ToArray();
        return sitePlan ? ApplySiteLinks(sources, links) : ApplyClientLinks(sources, targets, links);
    }

    private static HaloNCentralLinkResult ApplyClientLinks(IReadOnlyList<ExternalEntity> sources, IReadOnlyList<ExternalEntity> targets, IReadOnlyList<EntityIntegrationLink> links)
    {
        var clientLinks = links
            .Where(link => link.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase) && link.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            .GroupBy(link => link.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var appliedLinks = 0;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !clientLinks.TryGetValue(source.Id, out var matches)) continue;
            var targetIds = matches.Select(match => match.TargetId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targetIds.Length == 1)
            {
                source.ExternalIds["NCentralCustomerId"] = targetIds[0];
                source.CustomFields["HaloNCentralIntegrationId"] = matches[0].IntegrationId;
                source.CustomFields["HaloNCentralIntegrationLinkId"] = matches[0].LinkId;
                source.CustomFields["HaloNCentralLinkedTargetName"] = matches[0].TargetName;
                appliedLinks++;
            }
            else if (targetIds.Length > 1)
            {
                source.CustomFields["HaloNCentralIntegrationConflict"] = $"HaloPSA N-central integration has multiple customer links for Halo client {source.Id}: {string.Join(", ", targetIds)}.";
            }
        }

        var targetsByHaloId = targets
            .Where(target => target.ExternalIds.TryGetValue("NCentralExternalId", out var externalId) && !string.IsNullOrWhiteSpace(externalId))
            .GroupBy(target => target.ExternalIds["NCentralExternalId"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var externalIdLinks = 0;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || source.ExternalIds.ContainsKey("NCentralCustomerId")) continue;
            if (!targetsByHaloId.TryGetValue(source.Id, out var matches) || matches.Length != 1) continue;
            source.ExternalIds["NCentralCustomerId"] = matches[0].Id;
            source.CustomFields["NCentralExternalIdLink"] = matches[0].Id;
            externalIdLinks++;
        }

        return new HaloNCentralLinkResult(appliedLinks, 0, 0, externalIdLinks);
    }

    private static HaloNCentralLinkResult ApplySiteLinks(IReadOnlyList<ExternalEntity> sources, IReadOnlyList<EntityIntegrationLink> links)
    {
        var siteLinks = links
            .Where(link => link.SourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase) && link.TargetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
            .GroupBy(link => link.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var clientLinks = links
            .Where(link => link.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase) && link.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            .GroupBy(link => link.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var appliedSiteLinks = 0;
        var appliedParentLinks = 0;
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Id) && siteLinks.TryGetValue(source.Id, out var siteMatches))
            {
                var targetIds = siteMatches.Select(match => match.TargetId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (targetIds.Length == 1)
                {
                    source.ExternalIds["NCentralSiteId"] = targetIds[0];
                    if (!string.IsNullOrWhiteSpace(siteMatches[0].ParentTargetId)) source.ExternalIds["NCentralCustomerId"] = siteMatches[0].ParentTargetId!;
                    source.CustomFields["HaloNCentralIntegrationId"] = siteMatches[0].IntegrationId;
                    source.CustomFields["HaloNCentralIntegrationLinkId"] = siteMatches[0].LinkId;
                    source.CustomFields["HaloNCentralLinkedTargetName"] = siteMatches[0].TargetName;
                    appliedSiteLinks++;
                }
                else if (targetIds.Length > 1)
                {
                    source.CustomFields["HaloNCentralIntegrationConflict"] = $"HaloPSA N-central integration has multiple site links for Halo site {source.Id}: {string.Join(", ", targetIds)}.";
                }
            }

            var haloClientId = source.GetExternalId("HaloPsaClientId");
            if (string.IsNullOrWhiteSpace(haloClientId) || !clientLinks.TryGetValue(haloClientId, out var clientMatches)) continue;
            var customerIds = clientMatches.Select(match => match.TargetId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (customerIds.Length == 1)
            {
                if (source.ExternalIds.TryGetValue("NCentralCustomerId", out var existingCustomerId) && !existingCustomerId.Equals(customerIds[0], StringComparison.OrdinalIgnoreCase))
                {
                    source.CustomFields["HaloNCentralIntegrationConflict"] = $"HaloPSA N-central integration links Halo site {source.Id} to parent N-central customer {existingCustomerId}, but parent Halo client {haloClientId} links to N-central customer {customerIds[0]}.";
                    continue;
                }

                source.ExternalIds["NCentralCustomerId"] = customerIds[0];
                if (!string.IsNullOrWhiteSpace(clientMatches[0].TargetName)) source.CustomFields["NCentralCustomerName"] = clientMatches[0].TargetName;
                appliedParentLinks++;
            }
            else if (customerIds.Length > 1)
            {
                source.CustomFields["HaloNCentralIntegrationConflict"] = $"HaloPSA N-central integration has multiple customer links for parent Halo client {haloClientId}: {string.Join(", ", customerIds)}.";
            }
        }

        return new HaloNCentralLinkResult(0, appliedSiteLinks, appliedParentLinks, 0);
    }

    private static EntityIntegrationLink ToIntegrationLink(EntitySyncLookup lookup)
    {
        return new EntityIntegrationLink
        {
            SourceVendor = Property(lookup, "SourceVendor"),
            SourceEntityType = Property(lookup, "SourceEntityType"),
            SourceId = Property(lookup, "SourceId"),
            SourceName = lookup.Name,
            TargetVendor = Property(lookup, "TargetVendor"),
            TargetEntityType = Property(lookup, "TargetEntityType"),
            TargetId = Property(lookup, "TargetId"),
            TargetName = Property(lookup, "TargetName"),
            IntegrationId = Property(lookup, "IntegrationId"),
            LinkId = lookup.Id,
            ParentTargetId = OptionalProperty(lookup, "ParentTargetId"),
            Primary = bool.TryParse(OptionalProperty(lookup, "Primary"), out var primary) && primary
        };
    }

    private static string Property(EntitySyncLookup lookup, string name) => OptionalProperty(lookup, name) ?? string.Empty;

    private static string? OptionalProperty(EntitySyncLookup lookup, string name) => lookup.Properties.TryGetValue(name, out var value) ? value : null;
}
