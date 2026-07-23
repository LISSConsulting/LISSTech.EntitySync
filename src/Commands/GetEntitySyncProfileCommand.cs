using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncProfile")]
[OutputType(typeof(EntitySyncProfileInfo))]
public sealed class GetEntitySyncProfileCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        foreach (var profile in EntitySyncProfileStore.ListProfiles()) WriteObject(profile);
    }
}
