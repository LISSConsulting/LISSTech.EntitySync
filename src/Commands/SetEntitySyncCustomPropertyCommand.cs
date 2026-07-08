using System.Management.Automation;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Set, "EntitySyncCustomProperty", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(EntityWriteResult))]
public sealed class SetEntitySyncCustomPropertyCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("NCentral")]
    public string Vendor { get; set; } = string.Empty;

    [Parameter(Position = 1)]
    [ValidateSet("Customer")]
    public string EntityType { get; set; } = "Customer";

    [Parameter(Mandatory = true, Position = 2)]
    [Alias("CustomerId")]
    public string Id { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 3)]
    [Alias("PropertyName")]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 4)]
    [AllowEmptyString]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter Apply { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            var adapter = ConnectionRegistry.Get(Vendor) as NCentralEntityAdapter ?? throw new InvalidOperationException("Set-EntitySyncCustomProperty currently supports only N-central connections.");
            if (!EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("N-central custom property updates currently support EntityType Customer.");

            if (!Apply)
            {
                WriteWarning("No changes will be made unless -Apply is specified. -WhatIf is still supported when applying.");
                WriteObject(new EntityWriteResult { Vendor = Vendor, EntityType = EntityType, Id = Id, Action = "UpdateOrganizationProperties", Success = true, Message = $"Planned only; pass -Apply to set custom property '{Name}'." });
                return;
            }

            if (ShouldProcess($"{Vendor} {EntityType} {Id}", $"Set custom property '{Name}'"))
            {
                WriteObject(adapter.SetOrganizationCustomPropertyAsync(Id, Name, Value, CancellationToken.None).GetAwaiter().GetResult());
            }
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "SetEntitySyncCustomPropertyFailed", ErrorCategory.WriteError, Id));
        }
    }
}
