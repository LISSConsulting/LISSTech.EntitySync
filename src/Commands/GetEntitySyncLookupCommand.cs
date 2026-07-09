using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncLookup")]
[OutputType(typeof(EntitySyncLookup))]
public sealed class GetEntitySyncLookupCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral", "LCAT", "LTAC")]
    public string Vendor { get; set; } = string.Empty;

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    private static string NormalizeVendor(string vendor) =>
        vendor.Equals("LTAC", StringComparison.OrdinalIgnoreCase) ? "LCAT" : vendor;

    public object? GetDynamicParameters()
    {
        dynamicParameters = new RuntimeDefinedParameterDictionary();
        var lookupTypes = EntitySyncLookupTypes.ForVendor(NormalizeVendor(Vendor));
        if (lookupTypes.Count > 0) AddTypeParameter(lookupTypes.ToArray());
        return dynamicParameters;
    }

    protected override void EndProcessing()
    {
        try
        {
            var normalizedVendor = NormalizeVendor(Vendor);
            var lookupTypes = EntitySyncLookupTypes.ForVendor(normalizedVendor);
            if (lookupTypes.Count == 0) return;

            var type = DynamicValue<string?>("Type", null) ?? throw new InvalidOperationException("Type is required.");
            var adapter = ConnectionRegistry.Get(normalizedVendor);
            var traces = new ConcurrentQueue<string>();
            var progress = new ConcurrentQueue<EntitySyncProgress>();
            if (adapter is HaloEntityAdapter haloAdapter)
            {
                haloAdapter.Trace = traces.Enqueue;
                haloAdapter.Progress = progress.Enqueue;
            }

            IReadOnlyList<EntitySyncLookup> lookups;
            try
            {
                var task = Task.Run(() => adapter.GetLookupsAsync(type, CancellationToken.None));
                while (!task.IsCompleted)
                {
                    DrainMessages(traces, progress);
                    Thread.Sleep(100);
                }

                lookups = task.GetAwaiter().GetResult();
            }
            finally
            {
                if (adapter is HaloEntityAdapter completedHaloAdapter)
                {
                    completedHaloAdapter.Trace = null;
                    completedHaloAdapter.Progress = null;
                }
            }

            DrainMessages(traces, progress);
            WriteProgress(new ProgressRecord(1, "Get EntitySync lookup", "Complete") { RecordType = ProgressRecordType.Completed });
            foreach (var lookup in lookups) WriteObject(lookup);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "GetEntitySyncLookupFailed", ErrorCategory.ReadError, NormalizeVendor(Vendor)));
        }
    }
    private void DrainMessages(ConcurrentQueue<string> traces, ConcurrentQueue<EntitySyncProgress> progress)
    {
        while (traces.TryDequeue(out var trace)) WriteVerbose(trace);
        while (progress.TryDequeue(out var update)) WriteProgress(new ProgressRecord(1, update.Activity, update.Status) { PercentComplete = update.PercentComplete });
    }

    private void AddTypeParameter(params string[] validValues)
    {
        if (dynamicParameters == null) return;
        var attributes = new Collection<Attribute>
        {
            new ParameterAttribute { Mandatory = false, Position = 1 },
            new ValidateSetAttribute(validValues)
        };
        dynamicParameters.Add("Type", new RuntimeDefinedParameter("Type", typeof(string), attributes) { Value = validValues[0] });
    }

    private T DynamicValue<T>(string name, T defaultValue)
    {
        if (dynamicParameters != null && dynamicParameters.TryGetValue(name, out var parameter) && parameter.Value is T value) return value;
        return defaultValue;
    }
}
