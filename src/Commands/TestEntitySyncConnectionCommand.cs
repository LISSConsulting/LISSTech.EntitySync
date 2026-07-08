using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsDiagnostic.Test, "EntitySyncConnection")]
[OutputType(typeof(bool))]
public sealed class TestEntitySyncConnectionCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral", "LCAT", "LTAC")]
    public string Vendor { get; set; } = string.Empty;

    /// <summary>
    /// LCAT connection tests may be requested with the `LTAC` alias, but every downstream result and
    /// error must still identify the vendor as `LCAT` (spec FR-002).
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) =>
        vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase) ? "LCAT" : vendor;

    protected override void EndProcessing()
    {
        try
        {
            Vendor = NormalizeVendorAlias(Vendor);
            if (Vendor.Equals("LCAT", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotImplementedException("LCAT connection testing is implemented in a later EntitySync task.");
            }

            WriteObject(ConnectionRegistry.Get(Vendor).TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "TestEntitySyncConnectionFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }
}
