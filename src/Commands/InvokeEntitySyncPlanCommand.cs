using System.Management.Automation;
using System.Text.RegularExpressions;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.LCAT;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsLifecycle.Invoke, "EntitySyncPlan", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(EntityWriteResult))]
public sealed class InvokeEntitySyncPlanCommand : PSCmdlet
{
    private static readonly Regex LcatSlugPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$", RegexOptions.Compiled);

    [Parameter(Mandatory = true, ValueFromPipeline = true, Position = 0)]
    public EntitySyncPlan Plan { get; set; } = default!;

    [Parameter]
    public SwitchParameter Apply { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    [Parameter]
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";

    protected override void ProcessRecord()
    {
        try
        {
            if (!Apply) WriteWarning("No changes will be made unless -Apply is specified. -WhatIf is still supported when applying.");
            var mapper = new DefaultEntityMapper();
            var options = new MatchOptions { TargetCustomFieldName = TargetCustomFieldName };

            if (Plan.TargetVendor.Equals("LCAT", StringComparison.OrdinalIgnoreCase))
            {
                ApplyLcatBatch(mapper, options);
                WriteProgress(new ProgressRecord(1, "Invoke EntitySync plan", "Complete") { RecordType = ProgressRecordType.Completed });
                return;
            }

            for (var i = 0; i < Plan.Items.Count; i++)
            {
                var item = Plan.Items[i];
                var percent = (int)Math.Round((double)(i + 1) / Math.Max(1, Plan.Items.Count) * 100);
                WriteProgress(new ProgressRecord(1, "Invoke EntitySync plan", $"{item.Action} {i + 1}/{Plan.Items.Count}: {item.Source.Name}") { PercentComplete = percent });
                if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResult(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = "Review", Success = false, Message = "Item requires review before apply." });
                    continue;
                }
                if (!Apply)
                {
                    WriteResult(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = item.Action, Success = true, Message = "Planned only; pass -Apply to write." });
                    continue;
                }

                if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    var adapter = ConnectionRegistry.Get(Plan.TargetVendor);
                    var request = mapper.MapCreate(item.Source, Plan.TargetVendor, Plan.TargetEntityType, options);
                    if (Plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                    if (ShouldProcess(item.Source.Name, "Create target entity in " + Plan.TargetVendor))
                    {
                        WriteResultAndIntegrationLink(item, request, adapter.CreateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    }
                    continue;
                }

                if (item.Action.Equals("Link", StringComparison.OrdinalIgnoreCase) && item.Target != null)
                {
                    var adapter = ConnectionRegistry.Get(Plan.TargetVendor);
                    var request = mapper.MapUpdate(item.Source, item.Target, options);
                    if (Plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                    if (ShouldProcess(item.Target.Name, $"Set {TargetCustomFieldName} from {item.Source.Name}"))
                    {
                        WriteResultAndIntegrationLink(item, request, adapter.UpdateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    }
                }

                if (item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase) && item.Target != null)
                {
                    var adapter = ConnectionRegistry.Get(Plan.TargetVendor);
                    var request = mapper.MapUpdate(item.Source, item.Target, options);
                    if (Plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                    if (ShouldProcess(item.Target.Name, $"Update target entity from {item.Source.Name}"))
                    {
                        WriteResultAndIntegrationLink(item, request, adapter.UpdateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    }
                }
            }

            WriteProgress(new ProgressRecord(1, "Invoke EntitySync plan", "Complete") { RecordType = ProgressRecordType.Completed });
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "InvokeEntitySyncPlanFailed", ErrorCategory.WriteError, Plan));
        }
    }

    private void ApplyLcatBatch(DefaultEntityMapper mapper, MatchOptions options)
    {
        var batchItems = new List<(EntitySyncPlanItem Item, LCATCustomerScopeRequest Request)>();
        var candidateItems = new List<(EntitySyncPlanItem Item, LCATCustomerScopeRequest Request)>();
        var resultCount = 0;

        for (var i = 0; i < Plan.Items.Count; i++)
        {
            var item = Plan.Items[i];
            var percent = (int)Math.Round((double)(i + 1) / Math.Max(1, Plan.Items.Count) * 100);
            WriteProgress(new ProgressRecord(1, "Invoke EntitySync plan", $"{item.Action} {i + 1}/{Plan.Items.Count}: {item.Source.Name}") { PercentComplete = percent });

            if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
            {
                WriteResult(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = "Review", Success = false, Message = "Item requires review before apply." });
                resultCount++;
                continue;
            }
            if (!Apply)
            {
                WriteResult(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = item.Action, Success = true, Message = "Planned only; pass -Apply to write." });
                resultCount++;
                continue;
            }
            if (!IsApprovedLcatAction(item.Action)) continue;

            var request = item.Target != null
                ? mapper.MapUpdate(item.Source, item.Target, options)
                : mapper.MapCreate(item.Source, Plan.TargetVendor, Plan.TargetEntityType, options);
            candidateItems.Add((item, ToLcatCustomerScopeRequest(request)));
        }

        var duplicateIds = candidateItems
            .Select(candidate => NormalizeLcatValue(candidate.Request.NCentralCustomerId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateSlugs = candidateItems
            .Select(candidate => NormalizeLcatValue(candidate.Request.Slug))
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .GroupBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidateItems)
        {
            var validationErrors = ValidateLcatCustomerScopeRequest(candidate.Item, candidate.Request, duplicateIds, duplicateSlugs);
            if (validationErrors.Count > 0)
            {
                WriteResult(new EntityWriteResult
                {
                    Vendor = Plan.TargetVendor,
                    EntityType = Plan.TargetEntityType,
                    Id = candidate.Item.Target?.Id,
                    Action = candidate.Item.Action,
                    Success = false,
                    Message = "LCAT item skipped before batch sync: " + string.Join("; ", validationErrors) + "."
                });
                resultCount++;
                continue;
            }

            batchItems.Add(candidate);
        }

        if (batchItems.Count == 0)
        {
            if (resultCount == 0)
            {
                WriteResult(new EntityWriteResult
                {
                    Vendor = Plan.TargetVendor,
                    EntityType = Plan.TargetEntityType,
                    Action = "None",
                    Success = true,
                    Message = "No approved LCAT customer-scope items were eligible for batch sync."
                });
            }

            return;
        }
        if (!ShouldProcess($"{batchItems.Count} customer scope(s)", "Sync approved customer scopes to LCAT"))
        {
            if (IsWhatIfRequested()) WriteLcatWhatIfPreview(batchItems);
            return;
        }

        var adapter = ConnectionRegistry.Get(Plan.TargetVendor) as LCATEntityAdapter
            ?? throw new InvalidOperationException("LCAT adapter is required to apply an LCAT customer scope batch.");
        var syncResult = adapter.SyncCustomerScopesAsync(batchItems.Select(b => b.Request).ToList(), CancellationToken.None).GetAwaiter().GetResult();

        foreach (var (item, _) in batchItems)
        {
            WriteResult(new EntityWriteResult
            {
                Vendor = Plan.TargetVendor,
                EntityType = Plan.TargetEntityType,
                Id = item.Target?.Id,
                Action = item.Action,
                Success = true,
                Message = $"LCAT batch sync applied (inserted {syncResult.InsertedCount}, updated {syncResult.UpdatedCount}, retired {syncResult.RetiredCount}, active {syncResult.ActiveCount}).",
                Raw = syncResult
            });
        }
    }

    private static bool IsApprovedLcatAction(string action)
    {
        return action.Equals("Create", StringComparison.OrdinalIgnoreCase)
            || action.Equals("Update", StringComparison.OrdinalIgnoreCase)
            || action.Equals("Link", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsWhatIfRequested()
    {
        return MyInvocation.BoundParameters.TryGetValue("WhatIf", out var value)
            && (value is not SwitchParameter switchParameter || switchParameter.ToBool());
    }

    private void WriteLcatWhatIfPreview(IReadOnlyList<(EntitySyncPlanItem Item, LCATCustomerScopeRequest Request)> batchItems)
    {
        foreach (var (item, request) in batchItems)
        {
            WriteResult(new EntityWriteResult
            {
                Vendor = Plan.TargetVendor,
                EntityType = Plan.TargetEntityType,
                Id = item.Target?.Id ?? request.NCentralCustomerId,
                Action = item.Action,
                Success = true,
                Message = $"LCAT batch sync preview; no write performed because -WhatIf was specified. Would sync customer scope '{request.DisplayName}' with ncentral_customer_id '{request.NCentralCustomerId}'.",
                Raw = request
            });
        }
    }

    private static List<string> ValidateLcatCustomerScopeRequest(EntitySyncPlanItem item, LCATCustomerScopeRequest request, ISet<string> duplicateIds, ISet<string> duplicateSlugs)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.DisplayName)) errors.Add("display_name is required");
        if (string.IsNullOrWhiteSpace(request.NCentralCustomerId)) errors.Add("ncentral_customer_id is required");
        if (string.IsNullOrWhiteSpace(request.Slug)) errors.Add("slug is required");
        else if (!LcatSlugPattern.IsMatch(request.Slug)) errors.Add("slug must match the LCAT customer-scope contract");
        if (item.Source.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.NCentralParentCustomerId))
        {
            errors.Add("ncentral_parent_customer_id is required for N-central site sources");
        }

        var normalizedCustomerId = NormalizeLcatValue(request.NCentralCustomerId);
        var normalizedSlug = NormalizeLcatValue(request.Slug);
        if (!string.IsNullOrWhiteSpace(normalizedCustomerId) && duplicateIds.Contains(normalizedCustomerId))
        {
            errors.Add($"duplicate ncentral_customer_id '{request.NCentralCustomerId}'");
        }
        if (!string.IsNullOrWhiteSpace(normalizedSlug) && duplicateSlugs.Contains(normalizedSlug))
        {
            errors.Add($"duplicate slug '{request.Slug}'");
        }

        return errors;
    }

    private static string NormalizeLcatValue(string? value) => value?.Trim() ?? string.Empty;

    private static LCATCustomerScopeRequest ToLcatCustomerScopeRequest(EntityWriteRequest request)
    {
        return new LCATCustomerScopeRequest
        {
            Slug = request.Fields.TryGetValue("slug", out var slug) ? slug as string ?? string.Empty : string.Empty,
            DisplayName = request.Fields.TryGetValue("display_name", out var displayName) ? displayName as string ?? string.Empty : string.Empty,
            NCentralCustomerId = request.Fields.TryGetValue("ncentral_customer_id", out var ncId) ? ncId as string ?? string.Empty : string.Empty,
            NCentralParentCustomerId = request.Fields.TryGetValue("ncentral_parent_customer_id", out var parentId) ? parentId as string : null
        };
    }

    private void WriteResult(EntityWriteResult result)
    {
        if (PassThru) WriteObject(result);
    }

    private void WriteResultAndIntegrationLink(EntitySyncPlanItem item, EntityWriteRequest request, EntityWriteResult result)
    {
        WriteResult(result);
        if (!result.Success) return;
        if (RequiresHaloNCentralSiteLink(item))
        {
            WriteHaloNCentralSiteLink(item, request, result);
            return;
        }

        if (!RequiresHaloNCentralClientLink(item)) return;

        var targetId = result.Id ?? item.Target?.Id;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            WriteResult(new EntityWriteResult { Vendor = "HaloPSA", EntityType = "NCentralIntegrationLink", Action = "ClientLink", Success = false, Message = "N-central write succeeded, but no customer ID was available to write the HaloPSA N-central client link." });
            return;
        }

        var haloAdapter = ConnectionRegistry.Get("HaloPSA") as HaloEntityAdapter ?? throw new InvalidOperationException("HaloPSA adapter is required to write the N-central integration client link.");
        var targetName = item.Target?.Name ?? NCentralEntityAdapter.SanitizeNCentralName(request.Name);
        WriteResult(haloAdapter.UpsertNCentralClientLinkAsync(item.Source.Id, item.Source.Name, targetId, targetName, CancellationToken.None).GetAwaiter().GetResult());
    }

    private bool RequiresHaloNCentralClientLink(EntitySyncPlanItem item)
    {
        return Plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && Plan.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            && Plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && Plan.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Source.Id);
    }

    private bool RequiresHaloNCentralSiteLink(EntitySyncPlanItem item)
    {
        return Plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && Plan.SourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && Plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && Plan.TargetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Source.Id);
    }

    private void WriteHaloNCentralSiteLink(EntitySyncPlanItem item, EntityWriteRequest request, EntityWriteResult result)
    {
        var targetId = result.Id ?? item.Target?.Id;
        var customerId = request.CustomFields.TryGetValue("NCentralCustomerId", out var linkedCustomerId) ? linkedCustomerId : item.Source.GetExternalId("NCentralCustomerId");
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(customerId))
        {
            WriteResult(new EntityWriteResult { Vendor = "HaloPSA", EntityType = "NCentralIntegrationLink", Action = "SiteLink", Success = false, Message = "N-central site write succeeded, but no site ID or parent customer ID was available to write the HaloPSA N-central site link." });
            return;
        }

        var haloAdapter = ConnectionRegistry.Get("HaloPSA") as HaloEntityAdapter ?? throw new InvalidOperationException("HaloPSA adapter is required to write the N-central integration site link.");
        var targetName = item.Target?.Name ?? NCentralEntityAdapter.SanitizeNCentralName(request.Name);
        var haloClientName = item.Source.GetCustomField("HaloPsaClientName") ?? string.Empty;
        WriteResult(haloAdapter.UpsertNCentralSiteLinkAsync(item.Source.Id, item.Source.Name, haloClientName, targetId, targetName, customerId, CancellationToken.None).GetAwaiter().GetResult());
    }

}
