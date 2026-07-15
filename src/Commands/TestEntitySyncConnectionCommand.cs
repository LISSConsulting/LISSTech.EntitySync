using System.Management.Automation;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsDiagnostic.Test, "EntitySyncConnection")]
[OutputType(typeof(bool))]
public sealed class TestEntitySyncConnectionCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ArgumentCompleter(typeof(EntitySyncVendorCompleter))]
    public string Vendor { get; set; } = string.Empty;

    /// <summary>
    /// LTAC values are normalized to the cmdlet-facing AgentController vendor name.
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) => EntitySyncVendors.Normalize(vendor);

    protected override void EndProcessing()
    {
        try
        {
            Vendor = NormalizeVendorAlias(Vendor);
            WriteObject(ConnectionRegistry.Get(Vendor).TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "TestEntitySyncConnectionFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }
}
