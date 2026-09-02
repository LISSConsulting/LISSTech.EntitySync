using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.BillCom;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Adapters.SophosCentral;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Planning;
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
    [ValidateSet("HaloPSA", "NetSuite", "NCentral", "Bill.com", "BillCom", "BILL", "BillSpend", "Sophos Central", "SophosCentral", "Sophos")]
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
        if (!string.IsNullOrWhiteSpace(SourceVendor)) AddEntityTypeParameter("SourceEntityType", SourceEntityTypesForPlan());
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
    public string? SourceSearch { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int SourceCount { get; set; }

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
            SourceVendor = NormalizeVendorAlias(SourceVendor);
            using var sourceLease = ConnectionRegistry.Acquire(SourceVendor);
            using var targetLease = ConnectionRegistry.Acquire(TargetVendor);
            var sourceAdapter = sourceLease.Connection.Adapter;
            var targetAdapter = targetLease.Connection.Adapter;
            var sourceEntityType = DynamicValue<string?>("SourceEntityType", null) ?? DefaultEntityType(SourceVendor);
            var targetEntityType = DynamicValue<string?>("TargetEntityType", null) ?? DefaultEntityType(TargetVendor);
            var isLtacSnapshot = IsLtacSnapshotPlan(sourceEntityType);
            var authoritativeBillSnapshot = BillComPlanReconciliation.IsAuthoritativeRoute(SourceVendor, sourceEntityType, TargetVendor, targetEntityType);
            WriteProgress(new ProgressRecord(1, "New EntitySync plan", "Preparing source records") { PercentComplete = 0 });
            if (targetAdapter is HaloEntityAdapter && !FullTargetObjects)
            {
                WriteVerbose("Reading HaloPSA list records with include_custom_fields when the target custom field ID can be resolved. Falling back to full client records if needed.");
            }

            IReadOnlyList<ExternalEntity> sources;
            IReadOnlyList<ExternalEntity> targets;
            if (pipelineSources.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(SourceSearch) || SourceCount > 0)
                {
                    throw new InvalidOperationException("SourceSearch and SourceCount cannot be combined with pipeline source input.");
                }

                if (isLtacSnapshot || authoritativeBillSnapshot)
                {
                    throw new InvalidOperationException(isLtacSnapshot
                        ? "AgentController CustomerScope plans must read the complete N-central Customer and Site snapshot; pipeline input is not supported."
                        : "BILL.com exact-list plans must read the complete HaloPSA client list; pipeline input is not supported.");
                }
                sources = pipelineSources;
                WriteProgress(new ProgressRecord(1, "New EntitySync plan", $"Using {pipelineSources.Count} pipeline source record(s)") { PercentComplete = 30 });
                targets = TargetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
                    ? FetchPipelineTargetCandidates(targetAdapter, sources, targetEntityType)
                    : FetchEntitiesWithProgress(targetAdapter, BuildTargetQuery(targetAdapter, targetEntityType), "Reading target records", 30, 70);
            }
            else if (isLtacSnapshot)
            {
                if (!string.IsNullOrWhiteSpace(SourceSearch) || SourceCount > 0)
                {
                    throw new InvalidOperationException("AgentController CustomerScope plans require a complete source snapshot; SourceSearch and SourceCount are not supported.");
                }

                var customers = FetchEntitiesWithProgress(sourceAdapter, BuildSourceQuery(sourceAdapter, "Customer"), "Reading N-central customers", 0, 20);
                var sites = FetchEntitiesWithProgress(sourceAdapter, BuildSourceQuery(sourceAdapter, "Site"), "Reading N-central sites", 20, 40);
                sources = customers.Concat(sites).ToArray();
                targets = FetchEntitiesWithProgress(targetAdapter, BuildTargetQuery(targetAdapter, targetEntityType), "Reading target records", 40, 70);
            }
            else if (authoritativeBillSnapshot && (!string.IsNullOrWhiteSpace(SourceSearch) || SourceCount > 0))
            {
                throw new InvalidOperationException("BILL.com exact-list plans require a complete HaloPSA client list; SourceSearch and SourceCount are not supported.");
            }
            else
            {
                (sources, targets) = FetchSourceAndTargetEntities(
                    sourceAdapter,
                    BuildSourceQuery(sourceAdapter, sourceEntityType),
                    targetAdapter,
                    BuildTargetQuery(targetAdapter, targetEntityType));
            }

            var usingHaloNCentralLinks = HaloNCentralPlanLinks.IsCustomerPlan(SourceVendor, sourceEntityType, TargetVendor, targetEntityType, sourceAdapter);
            var usingHaloNCentralSiteLinks = HaloNCentralPlanLinks.IsSitePlan(SourceVendor, sourceEntityType, TargetVendor, targetEntityType, sourceAdapter);
            if (usingHaloNCentralLinks || usingHaloNCentralSiteLinks)
            {
                var links = HaloNCentralPlanLinks.ApplyAsync(sources, targets, sourceAdapter, usingHaloNCentralSiteLinks, CancellationToken.None).GetAwaiter().GetResult();
                WriteVerbose($"Applied {links.ClientLinks} HaloPSA N-central client link(s), {links.SiteLinks} site link(s), {links.ParentLinks} parent link(s), and {links.ExternalIdLinks} N-central external ID link(s).");
            }

            var defaultLinkedIdName = usingHaloNCentralSiteLinks ? "NCentralSiteId" : "NCentralCustomerId";
            var sourceExternalIdName = EffectiveSourceExternalIdName(usingHaloNCentralLinks, usingHaloNCentralSiteLinks, defaultLinkedIdName);
            var targetCustomFieldName = EffectiveTargetCustomFieldName();
            var targetExternalIdName = (usingHaloNCentralLinks || usingHaloNCentralSiteLinks) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName)) ? defaultLinkedIdName : sourceExternalIdName;
            var options = new MatchOptions
            {
                SourceExternalIdName = sourceExternalIdName,
                TargetExternalIdName = targetExternalIdName,
                TargetCustomFieldName = targetCustomFieldName,
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
            BillComPlanReconciliation.AddApprovedTargetOperations(plan);

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

    private IReadOnlyList<ExternalEntity> FetchEntitiesWithProgress(IEntityAdapter adapter, EntityQuery query, string status, int startPercent, int endPercent)
    {
        var traces = new ConcurrentQueue<string>();
        var progress = new ConcurrentQueue<EntitySyncProgress>();
        if (adapter is HaloEntityAdapter haloAdapter) haloAdapter.Trace = traces.Enqueue;
        if (adapter is HaloEntityAdapter haloProgressAdapter) haloProgressAdapter.Progress = progress.Enqueue;
        if (adapter is NetSuiteEntityAdapter netSuiteAdapter) netSuiteAdapter.Trace = traces.Enqueue;
        if (adapter is NCentralEntityAdapter nCentralAdapter) nCentralAdapter.Trace = traces.Enqueue;
        if (adapter is BillComEntityAdapter billComAdapter) billComAdapter.Trace = traces.Enqueue;
        if (adapter is SophosCentralEntityAdapter sophosCentralAdapter) sophosCentralAdapter.Trace = traces.Enqueue;

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
            if (adapter is BillComEntityAdapter completedBillComAdapter) completedBillComAdapter.Trace = null;
            if (adapter is SophosCentralEntityAdapter completedSophosCentralAdapter) completedSophosCentralAdapter.Trace = null;
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
            var sourceExternalIdName = EffectiveSourceExternalIdName(false, false, SourceExternalIdName);
            var searchTerms = new[] { source.Name, source.GetExternalId(sourceExternalIdName) ?? source.Id }.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            RequiredCustomFieldName = isHaloTarget ? EffectiveTargetCustomFieldName() : null,
            ThrottleLimit = ThrottleLimit
        };
    }

    private string EffectiveSourceExternalIdName(bool usingHaloNCentralLinks, bool usingHaloNCentralSiteLinks, string defaultLinkedIdName)
    {
        if ((usingHaloNCentralLinks || usingHaloNCentralSiteLinks) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName))) return defaultLinkedIdName;
        if (SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && EntitySyncVendors.IsBillCom(TargetVendor)
            && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName)))
        {
            return BillComEntityAdapter.ClientExternalIdName;
        }
        if (EntitySyncVendors.IsBillCom(SourceVendor) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName))) return BillComEntityAdapter.ClientExternalIdName;
        if (EntitySyncVendors.IsSophosCentral(SourceVendor) && !MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName))) return SophosCentralEntityAdapter.TenantExternalIdName;
        return SourceExternalIdName;
    }

    private string EffectiveTargetCustomFieldName()
    {
        if (EntitySyncVendors.IsBillCom(SourceVendor) && TargetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) && !MyInvocation.BoundParameters.ContainsKey(nameof(TargetCustomFieldName)))
        {
            return BillComEntityAdapter.HaloClientCustomFieldName;
        }
        if (EntitySyncVendors.IsSophosCentral(SourceVendor) && TargetVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase) && !MyInvocation.BoundParameters.ContainsKey(nameof(TargetCustomFieldName)))
        {
            return SophosCentralEntityAdapter.HaloTenantCustomFieldName;
        }


        return TargetCustomFieldName;
    }

    private EntityQuery BuildSourceQuery(IEntityAdapter sourceAdapter, string sourceEntityType)
    {
        var enrichHaloClientForNCentralCustomer = SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && sourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase);
        var enrichHaloClientForBill = BillComPlanReconciliation.IsAuthoritativeRoute(SourceVendor, sourceEntityType, TargetVendor, "Client");
        var query = new EntityQuery
        {
            EntityType = sourceEntityType,
            Search = SourceSearch,
            IncludeInactive = IncludeInactive,
            FullObjects = enrichHaloClientForNCentralCustomer || enrichHaloClientForBill,
            IncludeSiteDetails = enrichHaloClientForNCentralCustomer,
            ThrottleLimit = ThrottleLimit
        };
        if (SourceCount > 0) query.Count = SourceCount;
        if (sourceAdapter is HaloEntityAdapter haloSourceAdapter)
        {
            query.RequiredCustomFieldName = enrichHaloClientForBill
                ? string.Join(',', haloSourceAdapter.NetSuiteCustomerIdField, haloSourceAdapter.NetSuiteCustomerNameField, BillComEntityAdapter.HaloClientCustomFieldName)
                : string.Join(',', haloSourceAdapter.NetSuiteCustomerIdField, haloSourceAdapter.NetSuiteCustomerNameField);
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
        if (EntitySyncVendors.IsBillCom(vendor)) return new[] { "Client" };
        return new[] { "Customer" };
    }

    private string[] SourceEntityTypesForPlan()
    {
        if (SourceVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase) && EntitySyncVendors.IsAgentController(TargetVendor))
        {
            return new[] { "CustomerScope", "Customer", "Site" };
        }

        return EntityTypesForVendor(SourceVendor);
    }

    private bool IsLtacSnapshotPlan(string sourceEntityType)
    {
        return SourceVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && EntitySyncVendors.IsAgentController(TargetVendor)
            && sourceEntityType.Equals("CustomerScope", StringComparison.OrdinalIgnoreCase);
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
        if (adapter is BillComEntityAdapter billComAdapter) billComAdapter.Trace = traces.Enqueue;
        if (adapter is SophosCentralEntityAdapter sophosCentralAdapter) sophosCentralAdapter.Trace = traces.Enqueue;
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
        if (adapter is BillComEntityAdapter billComAdapter) billComAdapter.Trace = null;
        if (adapter is SophosCentralEntityAdapter sophosCentralAdapter) sophosCentralAdapter.Trace = null;
    }
}
