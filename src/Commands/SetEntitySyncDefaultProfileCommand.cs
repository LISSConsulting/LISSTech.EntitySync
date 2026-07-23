using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Set, "EntitySyncDefaultProfile")]
public sealed class SetEntitySyncDefaultProfileCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        try
        {
            EntitySyncProfileStore.SetDefaultProfile(Name);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "SetEntitySyncDefaultProfileFailed", ErrorCategory.InvalidOperation, Name));
        }
    }
}
