using System.Management.Automation;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommunications.Connect, "EntitySyncProfile")]
[OutputType(typeof(EntitySyncConnection))]
public sealed class ConnectEntitySyncProfileCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            foreach (var output in ConnectProfile(Name)) WriteObject(output);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "ConnectEntitySyncProfileFailed", ErrorCategory.ConnectionError, Name));
        }
    }

    internal static IReadOnlyList<object> ConnectProfile(string? name)
    {
        var outputs = new List<object>();
        foreach (var vendorProfile in EntitySyncProfileStore.LoadProfile(name))
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Connect-EntitySyncVendor");
            ps.AddParameter("Vendor", vendorProfile.Vendor);
            foreach (var setting in vendorProfile.Settings)
            {
                ps.AddParameter(setting.Key, setting.Value);
            }

            var result = ps.Invoke();
            if (ps.HadErrors)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, ps.Streams.Error.Select(error => error.ToString())));
            }

            outputs.AddRange(result.Select(item => item.BaseObject));
        }

        return outputs;
    }
}
