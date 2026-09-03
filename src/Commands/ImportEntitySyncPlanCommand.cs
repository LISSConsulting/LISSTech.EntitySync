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
    [Parameter]
    public string? IdempotencyKey { get; set; }


    protected override void EndProcessing()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        var hasDurableEnvelope = PowerShellDurablePlanWorkbook.HasDurableEnvelope(resolved);
        if (hasDurableEnvelope)
        {
            if (!PowerShellControlRuntime.IsDurableConfigured)
                throw new InvalidOperationException(
                    "A durable workbook requires durable PowerShell control configuration.");
            if (string.IsNullOrWhiteSpace(IdempotencyKey))
                throw new InvalidOperationException(
                    "-IdempotencyKey is required for durable plan import.");
            using var control = PowerShellControlRuntime.Open();
            var manifest = PowerShellDurablePlanWorkbook.Read(
                resolved, control.TenantId, control.DataProtection);
            var imported = control.Commands.ImportPlanAsync(
                    control.TenantId,
                    manifest,
                    IdempotencyKey,
                    control.Actor,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            WriteObject(imported);
            return;
        }
        if (!string.IsNullOrWhiteSpace(IdempotencyKey))
            throw new InvalidOperationException(
                "-IdempotencyKey is valid only for a durable workbook.");


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
