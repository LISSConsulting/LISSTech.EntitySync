using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsLifecycle.Invoke, "EntitySyncChain", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High, DefaultParameterSetName = "Plan")]
[OutputType(typeof(FileInfo), typeof(EntityWriteResult))]
public sealed class InvokeEntitySyncChainCommand : PSCmdlet
{
    [Parameter(ParameterSetName = "Plan")]
    public string RootVendor { get; set; } = "NetSuite";

    [Parameter(ParameterSetName = "Plan")]
    public string HubVendor { get; set; } = "HaloPSA";

    [Parameter(ParameterSetName = "Plan")]
    public string[] LeafVendors { get; set; } = { "NCentral" };

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Plan")]
    [Alias("OutputDirectory")]
    public string Path { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Apply")]
    public string[] ReviewedPlanPath { get; set; } = Array.Empty<string>();

    [Parameter(Mandatory = true, ParameterSetName = "Apply")]
    public SwitchParameter Apply { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    [Parameter(ParameterSetName = "Plan")]
    public SwitchParameter IncludeInactive { get; set; }

    [Parameter(ParameterSetName = "Plan")]
    public SwitchParameter CreateMissing { get; set; }

    [Parameter(ParameterSetName = "Plan")]
    public SwitchParameter FullTargetObjects { get; set; }

    [Parameter(ParameterSetName = "Plan")]
    public int AutoLinkScore { get; set; } = 90;

    [Parameter(ParameterSetName = "Plan")]
    public int ReviewScore { get; set; } = 70;

    [Parameter]
    public string SourceExternalIdName { get; set; } = "NetSuiteInternalId";

    [Parameter]
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";

    [Parameter(ParameterSetName = "Plan")]
    [ValidateRange(0, int.MaxValue)]
    public int ThrottleLimit { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            if (ParameterSetName.Equals("Apply", StringComparison.OrdinalIgnoreCase))
            {
                ApplyReviewedPlans();
                return;
            }

            ExportChainPlans();
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "InvokeEntitySyncChainFailed", ErrorCategory.InvalidOperation, null));
        }
    }

    private void ExportChainPlans()
    {
        var outputDirectory = GetUnresolvedProviderPathFromPSPath(Path);
        Directory.CreateDirectory(outputDirectory);

        var edges = BuildEdges().ToArray();
        for (var i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            WriteProgress(new ProgressRecord(1, "Invoke EntitySync chain", $"Planning {edge.SourceVendor} -> {edge.TargetVendor}") { PercentComplete = (int)Math.Round((double)i / Math.Max(1, edges.Length) * 100) });
            var plan = NewPlan(edge.SourceVendor, edge.TargetVendor);
            var filePath = System.IO.Path.Combine(outputDirectory, DefaultFileName(plan, i + 1));
            EntitySyncPlanWorkbook.Write(plan, filePath);
            if (PassThru) WriteObject(new FileInfo(filePath));
        }

        WriteProgress(new ProgressRecord(1, "Invoke EntitySync chain", "Complete") { RecordType = ProgressRecordType.Completed });
        WriteWarning("Chain plans were exported for Excel review. No vendor writes were made. Apply reviewed plans with Invoke-EntitySyncChain -ReviewedPlanPath <files> -Apply.");
    }

    private IEnumerable<(string SourceVendor, string TargetVendor)> BuildEdges()
    {
        if (string.IsNullOrWhiteSpace(RootVendor)) throw new InvalidOperationException("RootVendor is required.");
        if (string.IsNullOrWhiteSpace(HubVendor)) throw new InvalidOperationException("HubVendor is required.");
        yield return (RootVendor, HubVendor);
        foreach (var leaf in LeafVendors.Where(leaf => !string.IsNullOrWhiteSpace(leaf)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return (HubVendor, leaf);
        }
    }

    private EntitySyncPlan NewPlan(string sourceVendor, string targetVendor)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddCommand("New-EntitySyncPlan")
            .AddParameter("SourceVendor", sourceVendor)
            .AddParameter("TargetVendor", targetVendor)
            .AddParameter("TargetCustomFieldName", TargetCustomFieldName)
            .AddParameter("AutoLinkScore", AutoLinkScore)
            .AddParameter("ReviewScore", ReviewScore)
            .AddParameter("ThrottleLimit", ThrottleLimit);
        if (MyInvocation.BoundParameters.ContainsKey(nameof(SourceExternalIdName))) ps.AddParameter("SourceExternalIdName", SourceExternalIdName);
        if (IncludeInactive) ps.AddParameter("IncludeInactive");
        if (CreateMissing) ps.AddParameter("CreateMissing");
        if (FullTargetObjects) ps.AddParameter("FullTargetObjects");

        var result = ps.Invoke<EntitySyncPlan>();
        if (ps.HadErrors) throw new InvalidOperationException(string.Join(Environment.NewLine, ps.Streams.Error.Select(error => error.ToString())));
        return result.Count == 1 ? result[0] : throw new InvalidOperationException($"Expected one plan for {sourceVendor} -> {targetVendor}, got {result.Count}.");
    }

    private void ApplyReviewedPlans()
    {
        var mapper = new DefaultEntityMapper();
        var options = new MatchOptions { TargetCustomFieldName = TargetCustomFieldName };
        var planPaths = ResolveReviewedPlanPaths().ToArray();
        if (planPaths.Length == 0) throw new InvalidOperationException("At least one reviewed plan path is required.");
        for (var planIndex = 0; planIndex < planPaths.Length; planIndex++)
        {
            var plan = ReadPlan(planPaths[planIndex]);
            if (!ShouldProcess($"{plan.SourceVendor} -> {plan.TargetVendor}", "Apply reviewed EntitySync plan")) continue;
            for (var i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i];
                WriteProgress(new ProgressRecord(1, "Invoke EntitySync chain", $"Plan {planIndex + 1}/{planPaths.Length}: {item.Action} {i + 1}/{plan.Items.Count}: {item.Source.Name}") { PercentComplete = (int)Math.Round((double)(i + 1) / Math.Max(1, plan.Items.Count) * 100) });
                if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResult(new EntityWriteResult { Vendor = plan.TargetVendor, EntityType = plan.TargetEntityType, Id = item.Target?.Id, Action = "Review", Success = false, Message = "Item requires review before apply." });
                    continue;
                }

                if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    var adapter = ConnectionRegistry.Get(plan.TargetVendor);
                    var request = mapper.MapCreate(item.Source, plan.TargetVendor, plan.TargetEntityType, options);
                    if (plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                    if (ShouldProcess(item.Source.Name, "Create target entity in " + plan.TargetVendor)) WriteResultAndIntegrationLink(plan, item, request, adapter.CreateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    continue;
                }

                if ((item.Action.Equals("Link", StringComparison.OrdinalIgnoreCase) || item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase)) && item.Target != null)
                {
                    var adapter = ConnectionRegistry.Get(plan.TargetVendor);
                    var request = mapper.MapUpdate(item.Source, item.Target, options);
                    if (plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)) request.Name = item.Source.Name;
                    if (ShouldProcess(item.Target.Name, $"{item.Action} target entity from {item.Source.Name}")) WriteResultAndIntegrationLink(plan, item, request, adapter.UpdateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                }
            }
        }

        WriteProgress(new ProgressRecord(1, "Invoke EntitySync chain", "Complete") { RecordType = ProgressRecordType.Completed });
    }

    private IEnumerable<string> ResolveReviewedPlanPaths()
    {
        foreach (var path in ReviewedPlanPath.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var resolved = GetResolvedProviderPathFromPSPath(path, out var provider);
            if (!provider.Name.Equals("FileSystem", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Reviewed plan path '{path}' must resolve to the FileSystem provider.");
            foreach (var filePath in resolved) yield return filePath;
        }
    }

    private static EntitySyncPlan ReadPlan(string path)
    {
        var resolved = System.IO.Path.GetFullPath(path);
        if (resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return EntitySyncPlanWorkbook.Read(resolved);
        var json = File.ReadAllText(resolved);
        return JsonSerializer.Deserialize<EntitySyncPlan>(json) ?? throw new InvalidOperationException($"Plan file '{resolved}' did not contain a valid EntitySync plan.");
    }

    private static string DefaultFileName(EntitySyncPlan plan, int sequence)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return $"EntitySyncChain-{sequence:00}-{SafeName(plan.SourceVendor)}-{SafeName(plan.SourceEntityType)}-to-{SafeName(plan.TargetVendor)}-{SafeName(plan.TargetEntityType)}-{timestamp}.xlsx";
    }

    private static string SafeName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalid.Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private void WriteResult(EntityWriteResult result)
    {
        if (PassThru) WriteObject(result);
    }

    private void WriteResultAndIntegrationLink(EntitySyncPlan plan, EntitySyncPlanItem item, EntityWriteRequest request, EntityWriteResult result)
    {
        WriteResult(result);
        if (!result.Success) return;
        if (RequiresHaloNCentralSiteLink(plan, item))
        {
            WriteHaloNCentralSiteLink(item, request, result);
            return;
        }

        if (!RequiresHaloNCentralClientLink(plan, item)) return;

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

    private static bool RequiresHaloNCentralClientLink(EntitySyncPlan plan, EntitySyncPlanItem item)
    {
        return plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && plan.SourceEntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            && plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && plan.TargetEntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Source.Id);
    }

    private static bool RequiresHaloNCentralSiteLink(EntitySyncPlan plan, EntitySyncPlanItem item)
    {
        return plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && plan.SourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && plan.TargetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
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
