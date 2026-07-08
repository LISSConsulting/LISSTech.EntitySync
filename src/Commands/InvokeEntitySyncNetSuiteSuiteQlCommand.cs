using System.Management.Automation;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsLifecycle.Invoke, "EntitySyncNetSuiteSuiteQL")]
[OutputType(typeof(PSObject))]
public sealed class InvokeEntitySyncNetSuiteSuiteQlCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Query { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        try
        {
            var adapter = ConnectionRegistry.Get("NetSuite") as NetSuiteEntityAdapter
                ?? throw new InvalidOperationException("The active NetSuite connection is not a NetSuite adapter. Run Connect-EntitySyncVendor -Vendor NetSuite first.");
            adapter.Trace = WriteVerbose;
            try
            {
                var rows = adapter.InvokeSuiteQlAsync(Query, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var row in rows)
                {
                    var output = new PSObject();
                    foreach (var property in row) output.Properties.Add(new PSNoteProperty(property.Key, property.Value));
                    WriteObject(output);
                }
            }
            finally
            {
                adapter.Trace = null;
            }
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "InvokeEntitySyncNetSuiteSuiteQLFailed", ErrorCategory.InvalidOperation, null));
        }
    }
}
