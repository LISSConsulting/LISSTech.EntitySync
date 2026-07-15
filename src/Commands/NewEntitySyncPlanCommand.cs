using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.New, "EntitySyncPlan")]
[OutputType(typeof(EntitySyncPlan))]
public sealed class NewEntitySyncPlanCommand : PSCmdlet, IDynamicParameters
{
    private readonly List<ExternalEntity> pipelineSources = new();
    private RuntimeDefinedParameterDictionary? dynamicParameters;

    [Parameter(ValueFromPipeline = true)]
    public ExternalEntity? InputObject { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral")]
    public string SourceVendor { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ArgumentCompleter(typeof(EntitySyncVendorCompleter))]
    public string TargetVendor { get; set; } = string.Empty;

    /// <summary>
    /// LTAC values are normalized to the cmdlet-facing AgentController vendor name.
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) => EntitySyncVendors.Normalize(vendor);

    public object? GetDynamicParameters()
    {
        TargetVendor = NormalizeVendorAlias(TargetVendor);
        dynamicParameters = new RuntimeDefinedParameterDictionary();
        if (!string.IsNullOrWhiteSpace(SourceVendor)) AddEntityTypeParameter("SourceEntityType", EntityTypesForVendor(SourceVendor));
        if (!string.IsNullOrWhiteSpace(TargetVendor)) AddEntityTypeParameter("TargetEntityType", EntityTypesForVendor(TargetVendor));
        return dynamicParameters;
    }

    [Parameter]
    public SwitchParameter IncludeInactive { get; set; }

    [Parameter]
    public SwitchParameter CreateMissing { get; set; }

    [Parameter]
    public SwitchParameter FullTargetObjects { get; set; }

    [Parameter]
    public int AutoLinkScore { get; set; } = 90;

    [Parameter]
    public int ReviewScore { get; set; } = 70;

    [Parameter]
    public string SourceExternalIdName { get; set; } = "NetSuiteInternalId";

    [Parameter]
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ThrottleLimit { get; set; }

    protected override void ProcessRecord()
    {
        if (InputObject != null) pipelineSources.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        try
        {
            TargetVendor = NormalizeVendorAlias(TargetVendor);
            var sourceAdapter = ConnectionRegistry.Get(SourceVendor);
            var targetAdapter = ConnectionRegistry.Get(TargetVendor);
            var sourceEntityType = DynamicValue<string?>("SourceEntityType", null) ?? DefaultEntityType(SourceVendor);
            var targetEntityType = DynamicValue<string?>("TargetEntityType", null) ?? DefaultEntityType(TargetVendor);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", "Preparing source records") { PercentComplete = 0 });
            if (targetAdapter is HaloEntityAdapter && !FullTargetObjects)
            {
                WriteVerbose("Reading HaloPSA list records with include_custom_fields when the target custom field ID can be resolved. Falling back to full client records if needed.");
            }

            IReadOnlyList<ExternalEntity> sources;
            IReadOnlyList<ExternalEntity> targets;
            if (pipelineSources.Count > 0)
            {
                sources = pipelineSources;
                WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"Using {pipelineSources.Count} pipeline source record(s)") { PercentComplete = 30 });
                targets = TargetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
                    ? FetchPipelineTargetCandidates(targetAdapter, sources, targetEntityType)
                    : FetchEntitiesWithProgress(targetAdapter, BuildTargetQuery(targetAdapter, targetEntityType), "Reading target records", 30, 70);
            }
            else
            {
                (sources, targets) = FetchSourceAndTargetEntities(
                    sourceAdapter,
                    BuildSourceQuery(sourceAdapter, sourceEntityType),
                    targetAdapter,
                    BuildTargetQuery(targetAdapter, targetEntityType));
            }

            var usingHaloNCentralLinks = IsHaloToNCentralCustomerPlan(sourceEntityType, targetEntityType) && sourceAdapter is HaloEntityAdapter;
            var usingHaloNCentralSiteLinks = IsHaloToNCentralSitePlan(sourceEntityType, targetEntityType) && sourceAdapter is HaloEntityAdapter;
            if (usingHaloNCentralLinks)
            {
                ApplyHaloNCentralClientLinks(sources, sourceAdapter);
                ApplyNCentralExternalIdClientMarkers(sources, targets);
            }
            else if (usingHaloNCentralSiteLinks)
            {
                ApplyHaloNCentralSiteLinks(sources, sourceAdapter);
            }

            var defaultLinkedIdName = usingHaloNCentralSiteLinks ? "NCentralSiteId" : "NCentralCustomerId";
            var sourceExternalIdName = (usingHaloNCentralLinks || usingHaloNCentralSiteLinks) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName)) ? defaultLinkedIdName : SourceExternalIdName;
            var targetExternalIdName = (usingHaloNCentralLinks || usingHaloNCentralSiteLinks) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName)) ? defaultLinkedIdName : SourceExternalIdName;
            var options = new MatchOptions
            {
                SourceExternalIdName = sourceExternalIdName,
                TargetExternalIdName = targetExternalIdName,
                TargetCustomFieldName = TargetCustomFieldName,
                CreateMissing = CreateMissing,
                AutoLinkScore = AutoLinkScore,
                ReviewScore = ReviewScore
            };
            var matcher = new WeightedEntityMatcher();
            var matchIndex = matcher.CreateIndex(targets, options);
            var plan = new EntitySyncPlan { SourceVendor = SourceVendor, SourceEntityType = sourceEntityType, TargetVendor = TargetVendor, TargetEntityType = targetEntityType, TargetCandidates = targets.ToList() };
            var requiresAuthoritativeTarget = usingHaloNCentralLinks || usingHaloNCentralSiteLinks;
            var isLtacTarget = EntitySyncVendors.IsAgentController(TargetVendor);
            var duplicateLtacSourceIds = isLtacTarget ? FindDuplicateLtacSourceIdentifiers(sources) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateLtacSlugs = isLtacTarget ? FindDuplicateLtacSlugs(sources) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = MatchSources(sources, matchIndex, AutoLinkScore, ReviewScore, CreateMissing, ThrottleLimit, sourceExternalIdName, requiresAuthoritativeTarget, isLtacTarget, duplicateLtacSourceIds, duplicateLtacSlugs);
            plan.Items.AddRange(items);

            WriteObject(plan);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", "Complete") { RecordType = ProgressRecordType.Completed });
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "NewEntitySyncPlanFailed", ErrorCategory.InvalidOperation, null));
        }
    }

    private EntitySyncPlanItem[] MatchSources(IReadOnlyList<ExternalEntity> sources, WeightedEntityMatcher.EntityMatchIndex matchIndex, int autoLinkScore, int reviewScore, bool createMissing, int throttleLimit, string sourceExternalIdName, bool requiresAuthoritativeTarget, bool isLtacTarget, IReadOnlySet<string> duplicateLtacSourceIds, IReadOnlySet<string> duplicateLtacSlugs)
    {
        var items = new EntitySyncPlanItem[sources.Count];
        var completed = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = EffectiveThrottleLimit(throttleLimit) };
        var task = Task.Run(() => Parallel.For(0, sources.Count, parallelOptions, i =>
        {
            items[i] = CreatePlanItem(sources[i], matchIndex, autoLinkScore, reviewScore, createMissing, sourceExternalIdName, requiresAuthoritativeTarget, isLtacTarget, duplicateLtacSourceIds, duplicateLtacSlugs);
            Interlocked.Increment(ref completed);
        }));

        while (!task.IsCompleted)
        {
            var current = Volatile.Read(ref completed);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"Matching {current}/{sources.Count} source record(s)") { PercentComplete = 70 + (int)Math.Round((double)current / Math.Max(1, sources.Count) * 30) });
            Thread.Sleep(200);
        }

        task.GetAwaiter().GetResult();
        WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"Matched {sources.Count} source record(s)") { PercentComplete = 100 });
        return items;
    }

    private static EntitySyncPlanItem CreatePlanItem(ExternalEntity source, WeightedEntityMatcher.EntityMatchIndex matchIndex, int autoLinkScore, int reviewScore, bool createMissing, string sourceExternalIdName, bool requiresAuthoritativeTarget, bool isLtacTarget, IReadOnlySet<string> duplicateLtacSourceIds, IReadOnlySet<string> duplicateLtacSlugs)
    {
        if (source.CustomFields.TryGetValue("HaloNCentralIntegrationConflict", out var conflict) && !string.IsNullOrWhiteSpace(conflict))
        {
            return new EntitySyncPlanItem { Source = source, Action = "Review", MatchType = "IntegrationLinkConflict", Reasons = { conflict } };
        }

        if (isLtacTarget && TryGetLtacSourceValidationErrors(source, duplicateLtacSourceIds, duplicateLtacSlugs, out var ltacValidationErrors))
        {
            return new EntitySyncPlanItem
            {
                Source = source,
                Action = "Review",
                MatchType = "LtacSourceInvalid",
                Reasons = ltacValidationErrors.ToList()
            };
        }

        var candidates = matchIndex.FindMatches(source);
        var best = candidates.FirstOrDefault();
        var authoritativeTargetId = source.GetExternalId(sourceExternalIdName);
        if (best == null)
        {
            if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId))
            {
                return new EntitySyncPlanItem
                {
                    Source = source,
                    Action = "Review",
                    MatchType = "IntegrationLinkTargetMissing",
                    Reasons = { $"HaloPSA N-central integration links this source to N-central target {authoritativeTargetId}, but that target was not found in the fetched N-central target set." }
                };
            }

            return new EntitySyncPlanItem { Source = source, Action = createMissing ? "Create" : "Review", MatchType = "NoMatch", Reasons = { "No target candidate found" } };
        }

        if (requiresAuthoritativeTarget && !string.IsNullOrWhiteSpace(authoritativeTargetId) && !best.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase))
        {
            return new EntitySyncPlanItem
            {
                Source = source,
                Action = "Review",
                MatchType = "IntegrationLinkTargetMissing",
                Reasons = { $"HaloPSA N-central integration links this source to N-central target {authoritativeTargetId}, but that target was not found in the fetched N-central target set." }
            };
        }

        if (!best.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase) && best.Score < reviewScore)
        {
            var reasons = best.Reasons.ToList();
            reasons.Add($"Best candidate '{best.Target.Name}' scored {best.Score}, below review threshold {reviewScore}; target left blank.");
            return new EntitySyncPlanItem { Source = source, Action = createMissing ? "Create" : "Review", MatchType = best.MatchType, Score = best.Score, Reasons = reasons };
        }

        var action = best.MatchType == "Linked" ? "Update" : best.Score >= autoLinkScore ? "Link" : best.Score >= reviewScore ? "Review" : createMissing ? "Create" : "Review";
        return new EntitySyncPlanItem { Source = source, Target = best.Target, Action = action, MatchType = best.MatchType, Score = best.Score, Reasons = best.Reasons };
    }

    private static HashSet<string> FindDuplicateLtacSourceIdentifiers(IReadOnlyList<ExternalEntity> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var sourceIdentifier = GetLtacSourceIdentifier(source);
            if (string.IsNullOrWhiteSpace(sourceIdentifier)) continue;
            if (!seen.Add(sourceIdentifier)) duplicates.Add(sourceIdentifier);
        }

        return duplicates;
    }

    private static HashSet<string> FindDuplicateLtacSlugs(IReadOnlyList<ExternalEntity> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var slug = GetLtacSlug(source);
            if (string.IsNullOrWhiteSpace(slug)) continue;
            if (!seen.Add(slug)) duplicates.Add(slug);
        }

        return duplicates;
    }

    private static bool TryGetLtacSourceValidationErrors(ExternalEntity source, IReadOnlySet<string> duplicateLtacSourceIds, IReadOnlySet<string> duplicateLtacSlugs, out string[] errors)
    {
        var validationErrors = new List<string>();
        if (!source.Vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            validationErrors.Add($"LTAC customer-scope sync only accepts N-central Customer or Site source records; source vendor '{source.Vendor}' is not supported.");
        }

        var sourceIdentifier = GetLtacSourceIdentifier(source);
        if (string.IsNullOrWhiteSpace(sourceIdentifier))
        {
            validationErrors.Add($"N-central {source.EntityType} source has no source identifier; LTAC customer scopes require a non-empty N-central source identifier.");
        }
        else if (duplicateLtacSourceIds.Contains(sourceIdentifier))
        {
            validationErrors.Add($"Duplicate N-central source identifier '{sourceIdentifier}' cannot be synced to LTAC customer scopes.");
        }

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            validationErrors.Add($"N-central {source.EntityType} {DisplaySourceId(source)} has no display name; LTAC customer scopes require a non-empty display name.");
        }

        if (source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(source.GetExternalId("NCentralCustomerId")))
        {
            validationErrors.Add($"N-central site {DisplaySourceId(source)} has no parent N-central customer identifier; LTAC customer scopes require the parent N-central customer identifier.");
        }

        var slug = GetLtacSlug(source);
        if (!DefaultEntityMapper.IsValidLtacSlug(slug))
        {
            validationErrors.Add($"N-central {source.EntityType} {DisplaySourceId(source)} cannot produce a safe LTAC customer-scope slug.");
        }
        else if (duplicateLtacSlugs.Contains(slug))
        {
            validationErrors.Add($"Duplicate LTAC customer-scope slug '{slug}' cannot be synced from more than one N-central source record.");
        }

        errors = validationErrors.ToArray();
        return errors.Length > 0;
    }

    private static string GetLtacSlug(ExternalEntity source)
    {
        var sourceIdentifier = GetLtacSourceIdentifier(source);
        var slugBasis = source.Name;
        if (source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            var parentContext = FirstNonEmpty(source.GetCustomField("NCentralCustomerName"), source.GetExternalId("NCentralCustomerId"));
            slugBasis = string.IsNullOrWhiteSpace(parentContext) ? source.Name : $"{parentContext} {source.Name}";
        }

        return DefaultEntityMapper.DeriveLtacSlug(slugBasis, sourceIdentifier);
    }

    private static string? GetLtacSourceIdentifier(ExternalEntity source)
    {
        if (source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(source.GetExternalId("NCentralSiteId"), source.Id);
        }

        if (source.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(source.GetExternalId("NCentralCustomerId"), source.Id);
        }

        return FirstNonEmpty(source.Id);
    }

    private static string DisplaySourceId(ExternalEntity source) =>
        string.IsNullOrWhiteSpace(source.Id) ? "(missing id)" : source.Id;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private void ApplyHaloNCentralClientLinks(IReadOnlyList<ExternalEntity> sources, IEntityAdapter sourceAdapter)
    {
        var links = ReadHaloNCentralLinks(sourceAdapter, 60, 70);
        var clientLinks = links
            .Where(link => link.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase) && link.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            .GroupBy(link => link.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var applied = 0;
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
                applied++;
            }
            else if (targetIds.Length > 1)
            {
                source.CustomFields["HaloNCentralIntegrationConflict"] = $"HaloPSA N-central integration has multiple customer links for Halo client {source.Id}: {string.Join(", ", targetIds)}.";
            }
        }

        WriteVerbose($"Applied {applied} HaloPSA N-central client link(s) as authoritative N-central customer IDs.");
    }

    private void ApplyHaloNCentralSiteLinks(IReadOnlyList<ExternalEntity> sources, IEntityAdapter sourceAdapter)
    {
        var links = ReadHaloNCentralLinks(sourceAdapter, 60, 70);
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

        WriteVerbose($"Applied {appliedSiteLinks} HaloPSA N-central site link(s) and {appliedParentLinks} parent customer link(s) for site planning.");
    }

    private IReadOnlyList<EntityIntegrationLink> ReadHaloNCentralLinks(IEntityAdapter sourceAdapter, int startPercent, int endPercent)
    {
        var traces = new ConcurrentQueue<string>();
        var progress = new ConcurrentQueue<EntitySyncProgress>();
        ConfigureAdapterEvents(sourceAdapter, traces, progress);
        try
        {
            var task = Task.Run(async () =>
            {
                var lookups = await sourceAdapter.GetLookupsAsync(EntitySyncLookupTypes.NCentralIntegrationLink, CancellationToken.None).ConfigureAwait(false);
                return lookups.Select(ToIntegrationLink).ToArray();
            });
            while (!task.IsCompleted)
            {
                DrainMessages(traces, progress, startPercent, endPercent);
                Thread.Sleep(200);
            }

            return task.GetAwaiter().GetResult();
        }
        finally
        {
            ClearAdapterEvents(sourceAdapter);
            DrainMessages(traces, progress, startPercent, endPercent);
        }
    }

    private void ApplyNCentralExternalIdClientMarkers(IReadOnlyList<ExternalEntity> sources, IReadOnlyList<ExternalEntity> targets)
    {
        var targetsByHaloId = targets
            .Where(target => target.ExternalIds.TryGetValue("NCentralExternalId", out var externalId) && !string.IsNullOrWhiteSpace(externalId))
            .GroupBy(target => target.ExternalIds["NCentralExternalId"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || source.ExternalIds.ContainsKey("NCentralCustomerId")) continue;
            if (!targetsByHaloId.TryGetValue(source.Id, out var matches) || matches.Length != 1) continue;
            source.ExternalIds["NCentralCustomerId"] = matches[0].Id;
            source.CustomFields["NCentralExternalIdLink"] = matches[0].Id;
            applied++;
        }

        if (applied > 0) WriteVerbose($"Applied {applied} N-central externalId HaloPSA link marker(s) as authoritative N-central customer IDs.");
    }

    private static EntityIntegrationLink ToIntegrationLink(EntitySyncLookup lookup)
    {
        return new EntityIntegrationLink
        {
            SourceVendor = LookupProperty(lookup, "SourceVendor"),
            SourceEntityType = LookupProperty(lookup, "SourceEntityType"),
            SourceId = LookupProperty(lookup, "SourceId"),
            SourceName = lookup.Name,
            TargetVendor = LookupProperty(lookup, "TargetVendor"),
            TargetEntityType = LookupProperty(lookup, "TargetEntityType"),
            TargetId = LookupProperty(lookup, "TargetId"),
            TargetName = LookupProperty(lookup, "TargetName"),
            IntegrationId = LookupProperty(lookup, "IntegrationId"),
            LinkId = lookup.Id,
            ParentTargetId = LookupPropertyOrNull(lookup, "ParentTargetId"),
            Primary = bool.TryParse(LookupPropertyOrNull(lookup, "Primary"), out var primary) && primary
        };
    }

    private static string LookupProperty(EntitySyncLookup lookup, string name) => LookupPropertyOrNull(lookup, name) ?? string.Empty;

    private static string? LookupPropertyOrNull(EntitySyncLookup lookup, string name)
    {
        return lookup.Properties.TryGetValue(name, out var value) ? value : null;
    }

    private IReadOnlyList<ExternalEntity> FetchEntitiesWithProgress(IEntityAdapter adapter, EntityQuery query, string status, int startPercent, int endPercent)
    {
        var traces = new ConcurrentQueue<string>();
        var progress = new ConcurrentQueue<EntitySyncProgress>();
        if (adapter is HaloEntityAdapter haloAdapter) haloAdapter.Trace = traces.Enqueue;
        if (adapter is HaloEntityAdapter haloProgressAdapter) haloProgressAdapter.Progress = progress.Enqueue;
        if (adapter is NetSuiteEntityAdapter netSuiteAdapter) netSuiteAdapter.Trace = traces.Enqueue;
        if (adapter is NCentralEntityAdapter nCentralAdapter) nCentralAdapter.Trace = traces.Enqueue;

        try
        {
            var started = DateTimeOffset.UtcNow;
            var nextProgress = DateTimeOffset.MinValue;
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", status) { PercentComplete = startPercent });
            var task = Task.Run(() => adapter.GetEntitiesAsync(query, CancellationToken.None));
            while (!task.IsCompleted)
            {
                DrainMessages(traces, progress, startPercent, endPercent);
                var now = DateTimeOffset.UtcNow;
                if (now >= nextProgress)
                {
                    var elapsed = (int)(now - started).TotalSeconds;
                    WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"{status} ({elapsed}s elapsed)") { PercentComplete = -1 });
                    nextProgress = now.AddSeconds(1);
                }

                Thread.Sleep(200);
            }

            var entities = task.GetAwaiter().GetResult();
            DrainMessages(traces, progress, startPercent, endPercent);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"{status}: {entities.Count} record(s)") { PercentComplete = endPercent });
            return entities;
        }
        finally
        {
            if (adapter is HaloEntityAdapter completedHaloAdapter)
            {
                completedHaloAdapter.Trace = null;
                completedHaloAdapter.Progress = null;
            }

            if (adapter is NetSuiteEntityAdapter completedNetSuiteAdapter) completedNetSuiteAdapter.Trace = null;
            if (adapter is NCentralEntityAdapter completedNCentralAdapter) completedNCentralAdapter.Trace = null;
        }
    }

    private (IReadOnlyList<ExternalEntity> Sources, IReadOnlyList<ExternalEntity> Targets) FetchSourceAndTargetEntities(IEntityAdapter sourceAdapter, EntityQuery sourceQuery, IEntityAdapter targetAdapter, EntityQuery targetQuery)
    {
        if (ReferenceEquals(sourceAdapter, targetAdapter) || EffectiveThrottleLimit(ThrottleLimit) <= 1)
        {
            var sources = FetchEntitiesWithProgress(sourceAdapter, sourceQuery, "Reading source records", 0, 30);
            var targets = FetchEntitiesWithProgress(targetAdapter, targetQuery, "Reading target records", 30, 70);
            return (sources, targets);
        }

        var sourceTraces = new ConcurrentQueue<string>();
        var sourceProgress = new ConcurrentQueue<EntitySyncProgress>();
        var targetTraces = new ConcurrentQueue<string>();
        var targetProgress = new ConcurrentQueue<EntitySyncProgress>();
        ConfigureAdapterEvents(sourceAdapter, sourceTraces, sourceProgress);
        ConfigureAdapterEvents(targetAdapter, targetTraces, targetProgress);

        try
        {
            var started = DateTimeOffset.UtcNow;
            var nextProgress = DateTimeOffset.MinValue;
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", "Reading source and target records") { PercentComplete = -1 });
            var sourceTask = Task.Run(() => sourceAdapter.GetEntitiesAsync(sourceQuery, CancellationToken.None));
            var targetTask = Task.Run(() => targetAdapter.GetEntitiesAsync(targetQuery, CancellationToken.None));
            while (!sourceTask.IsCompleted || !targetTask.IsCompleted)
            {
                DrainMessages(sourceTraces, sourceProgress, 0, 30);
                DrainMessages(targetTraces, targetProgress, 30, 70);
                var now = DateTimeOffset.UtcNow;
                if (now >= nextProgress)
                {
                    var elapsed = (int)(now - started).TotalSeconds;
                    var status = $"Reading source and target records ({elapsed}s elapsed)";
                    if (sourceTask.IsCompleted) status += "; source complete";
                    if (targetTask.IsCompleted) status += "; target complete";
                    WriteProgress(new ProgressRecord(1, "New EntitySync plan", status) { PercentComplete = -1 });
                    nextProgress = now.AddSeconds(1);
                }

                Thread.Sleep(200);
            }

            var sources = sourceTask.GetAwaiter().GetResult();
            var targets = targetTask.GetAwaiter().GetResult();
            DrainMessages(sourceTraces, sourceProgress, 0, 30);
            DrainMessages(targetTraces, targetProgress, 30, 70);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"Read {sources.Count} source record(s) and {targets.Count} target record(s)") { PercentComplete = 70 });
            return (sources, targets);
        }
        finally
        {
            ClearAdapterEvents(sourceAdapter);
            ClearAdapterEvents(targetAdapter);
        }
    }

    private IReadOnlyList<ExternalEntity> FetchPipelineTargetCandidates(IEntityAdapter targetAdapter, IReadOnlyList<ExternalEntity> sources, string targetEntityType)
    {
        var targets = new List<ExternalEntity>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var start = 30 + (int)Math.Round((double)i / Math.Max(1, sources.Count) * 40);
            var end = 30 + (int)Math.Round((double)(i + 1) / Math.Max(1, sources.Count) * 40);
            var searchTerms = new[] { source.Name, source.GetExternalId(SourceExternalIdName) ?? source.Id }.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var searchTerm in searchTerms)
            {
                var query = BuildTargetQuery(targetAdapter, targetEntityType);
                query.Search = searchTerm;
                var candidates = FetchEntitiesWithProgress(targetAdapter, query, $"Reading target candidates {i + 1}/{sources.Count}: {searchTerm}", start, end);
                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate.Id) || seenIds.Add(candidate.Id)) targets.Add(candidate);
                }
            }
        }

        return targets;
    }

    private EntityQuery BuildTargetQuery(IEntityAdapter targetAdapter, string targetEntityType)
    {
        var isHaloTarget = TargetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase);
        return new EntityQuery
        {
            EntityType = targetEntityType,
            IncludeInactive = true,
            FullObjects = FullTargetObjects,
            IncludeSiteDetails = isHaloTarget && FullTargetObjects,
            RequiredCustomFieldName = isHaloTarget ? TargetCustomFieldName : null,
            ThrottleLimit = ThrottleLimit
        };
    }

    private EntityQuery BuildSourceQuery(IEntityAdapter sourceAdapter, string sourceEntityType)
    {
        var enrichHaloClientForNCentralCustomer = SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase);
        var query = new EntityQuery
        {
            EntityType = sourceEntityType,
            IncludeInactive = IncludeInactive,
            FullObjects = enrichHaloClientForNCentralCustomer,
            IncludeSiteDetails = enrichHaloClientForNCentralCustomer,
            ThrottleLimit = ThrottleLimit
        };
        if (sourceAdapter is HaloEntityAdapter haloSourceAdapter)
        {
            query.RequiredCustomFieldName = string.Join(',', haloSourceAdapter.NetSuiteCustomerIdField, haloSourceAdapter.NetSuiteCustomerNameField);
        }

        return query;
    }

    private static int EffectiveThrottleLimit(int throttleLimit) => throttleLimit > 0 ? throttleLimit : Math.Max(1, Environment.ProcessorCount);

    private void DrainMessages(ConcurrentQueue<string> traces, ConcurrentQueue<EntitySyncProgress> progress, int startPercent, int endPercent)
    {
        while (traces.TryDequeue(out var trace)) WriteVerbose(trace);
        while (progress.TryDequeue(out var update))
        {
            var percent = update.PercentComplete >= 0
                ? startPercent + (int)Math.Round((double)update.PercentComplete / 100 * (endPercent - startPercent))
                : -1;
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", update.Status) { PercentComplete = percent });
        }
    }

    private void AddEntityTypeParameter(string name, params string[] validValues)
    {
        if (dynamicParameters == null) return;
        if (validValues.Length == 0) validValues = new[] { "Customer" };
        var attributes = new Collection<Attribute>
        {
            new ParameterAttribute(),
            new ValidateSetAttribute(validValues)
        };
        dynamicParameters.Add(name, new RuntimeDefinedParameter(name, typeof(string), attributes) { Value = validValues[0] });
    }

    private static string DefaultEntityType(string vendor)
    {
        return EntityTypesForVendor(vendor)[0];
    }

    private static string[] EntityTypesForVendor(string vendor)
    {
        if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return new[] { "Client", "Site" };
        if (vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) return new[] { "Customer", "Site" };
        return new[] { "Customer" };
    }

    private bool IsHaloToNCentralCustomerPlan(string sourceEntityType, string targetEntityType)
    {
        return SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            && targetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsHaloToNCentralSitePlan(string sourceEntityType, string targetEntityType)
    {
        return SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && targetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase);
    }

    private T DynamicValue<T>(string name, T defaultValue)
    {
        if (dynamicParameters != null && dynamicParameters.TryGetValue(name, out var parameter) && parameter.Value is T value)
        {
            return value;
        }

        return defaultValue;
    }

    private static void ConfigureAdapterEvents(IEntityAdapter adapter, ConcurrentQueue<string> traces, ConcurrentQueue<EntitySyncProgress> progress)
    {
        if (adapter is HaloEntityAdapter haloAdapter)
        {
            haloAdapter.Trace = traces.Enqueue;
            haloAdapter.Progress = progress.Enqueue;
        }

        if (adapter is NetSuiteEntityAdapter netSuiteAdapter) netSuiteAdapter.Trace = traces.Enqueue;
        if (adapter is NCentralEntityAdapter nCentralAdapter) nCentralAdapter.Trace = traces.Enqueue;
    }

    private static void ClearAdapterEvents(IEntityAdapter adapter)
    {
        if (adapter is HaloEntityAdapter haloAdapter)
        {
            haloAdapter.Trace = null;
            haloAdapter.Progress = null;
        }

        if (adapter is NetSuiteEntityAdapter netSuiteAdapter) netSuiteAdapter.Trace = null;
        if (adapter is NCentralEntityAdapter nCentralAdapter) nCentralAdapter.Trace = null;
    }
}
