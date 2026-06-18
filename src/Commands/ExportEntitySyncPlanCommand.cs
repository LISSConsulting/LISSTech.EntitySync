using System.Management.Automation;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsData.Export, "EntitySyncPlan")]
public sealed class ExportEntitySyncPlanCommand : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public EntitySyncPlan Plan { get; set; } = default!;

    [Parameter(Mandatory = true, Position = 0)]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        var json = JsonSerializer.Serialize(Plan, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resolved, json, new System.Text.UTF8Encoding(false));
    }
}
