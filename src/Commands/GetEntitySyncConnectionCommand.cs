using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncConnection")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class GetEntitySyncConnectionCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        foreach (var connection in ConnectionRegistry.Connections()) WriteObject(connection);
    }
}
