using System.Management.Automation;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncEntity")]
[OutputType(typeof(ExternalEntity))]
public sealed class GetEntitySyncEntityCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string Vendor { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1)]
    public string EntityType { get; set; } = string.Empty;

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    public SwitchParameter IncludeInactive { get; set; }

    [Parameter]
    public int Count { get; set; }

    [Parameter]
    public SwitchParameter FullObjects { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            var query = new EntityQuery { EntityType = EntityType, Search = Search, IncludeInactive = IncludeInactive, FullObjects = FullObjects };
            if (Count > 0) query.Count = Count;
            var entities = ConnectionRegistry.Get(Vendor).GetEntitiesAsync(query, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var entity in entities) WriteObject(entity);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "GetEntitySyncEntityFailed", ErrorCategory.ReadError, Vendor));
        }
    }
}
