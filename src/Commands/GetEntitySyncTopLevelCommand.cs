using System.Collections.Concurrent;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncTopLevel")]
[OutputType(typeof(EntitySyncTopLevel))]
public sealed class GetEntitySyncTopLevelCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA")]
    public string Vendor { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        try
        {
            var adapter = ConnectionRegistry.Get(Vendor);
            if (adapter is not HaloEntityAdapter haloAdapter) throw new NotSupportedException($"Top-level discovery is not supported for vendor '{Vendor}'.");

            var progress = new ConcurrentQueue<EntitySyncProgress>();
            haloAdapter.Progress = progress.Enqueue;
            IReadOnlyList<EntitySyncTopLevel> topLevels;
            try
            {
                var task = Task.Run(() => haloAdapter.GetTopLevelsAsync(CancellationToken.None));
                while (!task.IsCompleted)
                {
                    while (progress.TryDequeue(out var update)) WriteProgress(new ProgressRecord(1, update.Activity, update.Status) { PercentComplete = update.PercentComplete });
                    Thread.Sleep(100);
                }

                topLevels = task.GetAwaiter().GetResult();
            }
            finally
            {
                haloAdapter.Progress = null;
            }

            WriteProgress(new ProgressRecord(1, "Get EntitySync top levels", "Complete") { RecordType = ProgressRecordType.Completed });
            foreach (var topLevel in topLevels) WriteObject(topLevel);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "GetEntitySyncTopLevelFailed", ErrorCategory.ReadError, Vendor));
        }
    }
}
