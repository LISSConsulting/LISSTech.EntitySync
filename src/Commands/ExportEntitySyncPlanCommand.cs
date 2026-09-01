using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(
    VerbsData.Export,
    "EntitySyncPlan",
    DefaultParameterSetName = "Local")]
public sealed class ExportEntitySyncPlanCommand : PSCmdlet
{
    [Parameter(
        Mandatory = true,
        ValueFromPipeline = true,
        ParameterSetName = "Local")]
    public EntitySyncPlan? Plan { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = "Durable")]
    public Guid PlanId { get; set; }

    [Parameter(Mandatory = true, Position = 0)]
    [Alias("FilePath")]
    public string Path { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        if (ParameterSetName.Equals("Durable", StringComparison.Ordinal)
            || PowerShellControlRuntime.IsDurableConfigured)
        {
            if (!ParameterSetName.Equals("Durable", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Durable PowerShell control configuration requires -PlanId.");
            using var control = PowerShellControlRuntime.Open();
            var durablePlan = control.Plans.GetAsync(
                    control.TenantId, PlanId, CancellationToken.None)
                .GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException(
                    $"Durable plan '{PlanId}' was not found.");
            var items = new List<EntitySyncDurablePlanItem>(durablePlan.ItemCount);
            for (var page = 1; items.Count < durablePlan.ItemCount; page++)
            {
                var result = control.Plans.GetPageAsync(
                        control.TenantId,
                        PlanId,
                        page,
                        100,
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (result.Items.Count == 0)
                    throw new InvalidOperationException(
                        "The durable plan item pages are incomplete.");
                items.AddRange(result.Items);
            }
            var manifest = EntitySyncDurablePlanManifest.LoadPersisted(durablePlan, items);
            var first = manifest.Items.FirstOrDefault();
            var resolved = ResolveExportPath(
                manifest.Plan.PlanId.ToString(),
                first?.SourceVendor ?? "Unknown",
                first?.SourceEntityType ?? "Unknown",
                first?.TargetVendor ?? "Unknown",
                first?.TargetEntityType ?? "Unknown");
            if (!resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Durable plan export requires an .xlsx workbook path.");
            PowerShellDurablePlanWorkbook.Write(
                manifest, resolved, control.DataProtection);
            WriteExportResult(resolved);
            return;
        }

        var localPlan = Plan
            ?? throw new InvalidOperationException("-Plan is required for local export.");
        var localResolved = ResolveExportPath(
            localPlan.Id,
            localPlan.SourceVendor,
            localPlan.SourceEntityType,
            localPlan.TargetVendor,
            localPlan.TargetEntityType);
        if (localResolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            EntitySyncPlanWorkbook.Write(localPlan, localResolved);
            WriteExportResult(localResolved);
            return;
        }

        var json = JsonSerializer.Serialize(
            EntitySyncPlanArtifactSanitizer.Sanitize(localPlan),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            localResolved, json, new System.Text.UTF8Encoding(false));
        WriteExportResult(localResolved);
    }

    private string ResolveExportPath(
        string planId,
        string sourceVendor,
        string sourceEntityType,
        string targetVendor,
        string targetEntityType)
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        if (Directory.Exists(resolved)
            || string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(resolved)))
            return System.IO.Path.Combine(
                resolved,
                DefaultFileName(
                    planId,
                    sourceVendor,
                    sourceEntityType,
                    targetVendor,
                    targetEntityType,
                    ".xlsx"));
        return resolved;
    }

    private static string DefaultFileName(
        string planId,
        string sourceVendor,
        string sourceEntityType,
        string targetVendor,
        string targetEntityType,
        string extension)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return $"EntitySync-{SafeName(sourceVendor)}-{SafeName(sourceEntityType)}-to-" +
               $"{SafeName(targetVendor)}-{SafeName(targetEntityType)}-" +
               $"{SafeName(planId)}-{timestamp}{extension}";
    }

    private static string SafeName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalid.Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private void WriteExportResult(string path)
    {
        if (PassThru) WriteObject(new FileInfo(path));
    }
}
