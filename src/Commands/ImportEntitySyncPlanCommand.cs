using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsData.Import, "EntitySyncPlan")]
[OutputType(typeof(EntitySyncPlan))]
public sealed class ImportEntitySyncPlanCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Path { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        var json = File.ReadAllText(resolved);
        WriteObject(JsonSerializer.Deserialize<EntitySyncPlan>(json) ?? throw new InvalidOperationException("Plan file did not contain a valid EntitySync plan."));
    }
}
