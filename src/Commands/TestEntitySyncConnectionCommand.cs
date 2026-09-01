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
    [Parameter]
    public string? ConnectionId { get; set; }


    /// <summary>
    /// LTAC values are normalized to the cmdlet-facing AgentController vendor name.
    /// </summary>
    private static string NormalizeVendorAlias(string vendor) => EntitySyncVendors.Normalize(vendor);

    protected override void EndProcessing()
    {
        try
        {
            Vendor = NormalizeVendorAlias(Vendor);
            if (PowerShellControlRuntime.IsDurableConfigured)
            {
                using var control = PowerShellControlRuntime.Open();
                var definitions = control.Commands.ListConnectionsAsync(
                        control.TenantId, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var matches = definitions.Where(definition =>
                        definition.Vendor.Equals(Vendor, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrWhiteSpace(ConnectionId)
                            || definition.ConnectionId.Equals(
                                ConnectionId.Trim(), StringComparison.Ordinal)))
                    .Take(2).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(ConnectionId)
                            ? $"Exactly one durable {Vendor} connection is required; specify -ConnectionId."
                            : $"Durable connection '{ConnectionId}' was not found for {Vendor}.");
                var definition = matches[0];
                WriteObject(control.Connections.TestAsync(
                        control.TenantId,
                        definition.ConnectionId,
                        definition.Generation,
                        CancellationToken.None)
                    .GetAwaiter().GetResult());
                return;
            }

            using var lease = ConnectionRegistry.Acquire(Vendor);
            WriteObject(lease.Connection.Adapter.TestConnectionAsync(
                    CancellationToken.None)
                .GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "TestEntitySyncConnectionFailed", ErrorCategory.ConnectionError, Vendor));
        }
    }
}
