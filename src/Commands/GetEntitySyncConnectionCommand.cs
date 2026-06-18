using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncConnection")]
[OutputType(typeof(string))]
public sealed class GetEntitySyncConnectionCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        foreach (var name in ConnectionRegistry.Names()) WriteObject(name);
    }
}
