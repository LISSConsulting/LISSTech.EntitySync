using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Remove, "EntitySyncProfile", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveEntitySyncProfileCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        try
        {
            if (ShouldProcess(Name, "Remove EntitySync profile")) EntitySyncProfileStore.RemoveProfile(Name);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "RemoveEntitySyncProfileFailed", ErrorCategory.InvalidOperation, Name));
        }
    }
}
