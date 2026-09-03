using System.Management.Automation;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncConnection")]
[OutputType(typeof(EntitySyncConnection), typeof(EntitySyncControlConnectionInfo))]
public sealed class GetEntitySyncConnectionCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        if (!PowerShellControlRuntime.IsDurableConfigured)
        {
            foreach (var connection in ConnectionRegistry.Connections())
                WriteObject(connection);
            return;
        }

        using var control = PowerShellControlRuntime.Open();
        var connections = control.Commands.ListConnectionsAsync(
                control.TenantId, CancellationToken.None)
            .GetAwaiter().GetResult();
        foreach (var connection in connections)
            WriteObject(EntitySyncControlConnectionInfo.From(connection));
    }
}
