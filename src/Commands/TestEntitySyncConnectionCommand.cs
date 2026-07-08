using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsDiagnostic.Test, "EntitySyncConnection")]
[OutputType(typeof(bool))]
public sealed class TestEntitySyncConnectionCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral")]
    public string Vendor { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        try
        {
            WriteObject(ConnectionRegistry.Get(Vendor).TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "TestEntitySyncConnectionFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }
}
