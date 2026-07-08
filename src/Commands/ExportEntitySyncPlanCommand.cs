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
    [Alias("FilePath")]
    public string Path { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        var resolved = ResolveExportPath();
        if (resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            EntitySyncPlanWorkbook.Write(Plan, resolved);
            WriteExportResult(resolved);
            return;
        }

        var json = JsonSerializer.Serialize(Plan, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resolved, json, new System.Text.UTF8Encoding(false));
        WriteExportResult(resolved);
    }

    private string ResolveExportPath()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(Path);
        if (Directory.Exists(resolved)) return System.IO.Path.Combine(resolved, DefaultFileName(".xlsx"));
        if (string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(resolved))) return System.IO.Path.Combine(resolved, DefaultFileName(".xlsx"));
        return resolved;
    }

    private string DefaultFileName(string extension)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return $"EntitySync-{SafeName(Plan.SourceVendor)}-{SafeName(Plan.SourceEntityType)}-to-{SafeName(Plan.TargetVendor)}-{SafeName(Plan.TargetEntityType)}-{timestamp}{extension}";
    }

    private static string SafeName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalid.Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private void WriteExportResult(string path)
    {
        if (PassThru) WriteObject(new FileInfo(path));
    }
}
