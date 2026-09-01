using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsData.Import, "EntitySyncPlan")]
[OutputType(typeof(EntitySyncPlan), typeof(EntitySyncDurablePlan))]
public sealed class ImportEntitySyncPlanCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Path { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        if (PowerShellControlRuntime.IsDurableConfigured)
        {
            if (!resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Durable plan import requires an .xlsx workbook.");
            var manifest = PowerShellDurablePlanWorkbook.Read(resolved);
            using var control = PowerShellControlRuntime.Open();
            if (!manifest.Plan.TenantId.Equals(
                    control.TenantId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Workbook durable plan tenant does not match the PowerShell control tenant.");
            var existing = control.Plans.GetAsync(
                    control.TenantId,
                    manifest.Plan.PlanId,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            if (existing is null)
            {
                control.Plans.InsertAsync(
                        control.TenantId, manifest, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            else if (existing.PlanDigestSha256 != manifest.Plan.PlanDigestSha256)
            {
                throw new InvalidOperationException(
                    "A different immutable durable plan already uses this plan ID.");
            }
            WriteObject(manifest.Plan);
            return;
        }

        if (resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            WriteObject(EntitySyncPlanWorkbook.Read(resolved));
            return;
        }

        var json = File.ReadAllText(resolved);
        var plan = JsonSerializer.Deserialize<EntitySyncPlan>(json) ?? throw new InvalidOperationException("Plan file did not contain a valid EntitySync plan.");
        ReviewedPlanPolicy.PrepareForReview(plan);
        WriteObject(plan);
    }
}
