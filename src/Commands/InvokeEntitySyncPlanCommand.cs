using System.Management.Automation;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsLifecycle.Invoke, "EntitySyncPlan", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(EntityWriteResult))]
public sealed class InvokeEntitySyncPlanCommand : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true, Position = 0)]
    public EntitySyncPlan Plan { get; set; } = default!;

    [Parameter]
    public SwitchParameter Apply { get; set; }

    [Parameter]
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";

    protected override void ProcessRecord()
    {
        try
        {
            if (!Apply) WriteWarning("No changes will be made unless -Apply is specified. -WhatIf is still supported when applying.");
            var adapter = ConnectionRegistry.Get(Plan.TargetVendor);
            var mapper = new DefaultEntityMapper();
            var options = new MatchOptions { TargetCustomFieldName = TargetCustomFieldName };
            foreach (var item in Plan.Items)
            {
                if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                {
                    WriteObject(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = "Review", Success = false, Message = "Item requires review before apply." });
                    continue;
                }
                if (!Apply)
                {
                    WriteObject(new EntityWriteResult { Vendor = Plan.TargetVendor, EntityType = Plan.TargetEntityType, Id = item.Target?.Id, Action = item.Action, Success = true, Message = "Planned only; pass -Apply to write." });
                    continue;
                }

                if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    var request = mapper.MapCreate(item.Source, Plan.TargetVendor, Plan.TargetEntityType, options);
                    if (ShouldProcess(item.Source.Name, "Create target entity in " + Plan.TargetVendor))
                    {
                        WriteObject(adapter.CreateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    }
                    continue;
                }

                if (item.Action.Equals("Link", StringComparison.OrdinalIgnoreCase) && item.Target != null)
                {
                    var request = mapper.MapUpdate(item.Source, item.Target, options);
                    if (ShouldProcess(item.Target.Name, $"Set {TargetCustomFieldName} from {item.Source.Name}"))
                    {
                        WriteObject(adapter.UpdateEntityAsync(request, CancellationToken.None).GetAwaiter().GetResult());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "InvokeEntitySyncPlanFailed", ErrorCategory.WriteError, Plan));
        }
    }
}
