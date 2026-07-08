using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncEntity")]
[OutputType(typeof(ExternalEntity))]
public sealed class GetEntitySyncEntityCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite", "NCentral")]
    public string Vendor { get; set; } = string.Empty;

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    public object? GetDynamicParameters()
    {
        dynamicParameters = new RuntimeDefinedParameterDictionary();
        if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            AddEntityTypeParameter("Client", "Site");
        }
        else if (Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            AddEntityTypeParameter("Customer");
        }
        else if (Vendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase))
        {
            AddEntityTypeParameter("Customer", "Site");
        }

        return dynamicParameters;
    }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    public SwitchParameter IncludeInactive { get; set; }

    [Parameter]
    public int Count { get; set; }

    [Parameter]
    public SwitchParameter FullObjects { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ThrottleLimit { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            var entityType = DynamicValue<string?>("EntityType", null) ?? throw new InvalidOperationException("EntityType is required.");
            var query = new EntityQuery { EntityType = entityType, Search = Search, IncludeInactive = IncludeInactive, FullObjects = FullObjects, IncludeSiteDetails = FullObjects, ThrottleLimit = ThrottleLimit };
            if (Count > 0) query.Count = Count;
            var adapter = ConnectionRegistry.Get(Vendor);
            if (adapter is HaloEntityAdapter haloQueryAdapter && !FullObjects) query.RequiredCustomFieldName = string.Join(',', haloQueryAdapter.NetSuiteCustomerIdField, haloQueryAdapter.NetSuiteCustomerNameField);
            var traces = new ConcurrentQueue<string>();
            var progress = new ConcurrentQueue<EntitySyncProgress>();
            if (adapter is HaloEntityAdapter haloAdapter) haloAdapter.Trace = traces.Enqueue;
            if (adapter is HaloEntityAdapter haloProgressAdapter) haloProgressAdapter.Progress = progress.Enqueue;
            if (adapter is NetSuiteEntityAdapter netSuiteAdapter) netSuiteAdapter.Trace = traces.Enqueue;
            if (adapter is NCentralEntityAdapter nCentralAdapter) nCentralAdapter.Trace = traces.Enqueue;
            IReadOnlyList<ExternalEntity> entities;
            try
            {
                var activity = $"Get {Vendor} {entityType}";
                var started = DateTimeOffset.UtcNow;
                var nextProgress = DateTimeOffset.MinValue;
                WriteProgress(new ProgressRecord(1, activity, "Starting vendor read") { PercentComplete = -1 });
                var task = Task.Run(() => adapter.GetEntitiesAsync(query, CancellationToken.None));
                while (!task.IsCompleted)
                {
                    DrainMessages(traces, progress);
                    var now = DateTimeOffset.UtcNow;
                    if (now >= nextProgress)
                    {
                        var elapsed = (int)(now - started).TotalSeconds;
                        WriteProgress(new ProgressRecord(1, activity, $"Reading from {Vendor} ({elapsed}s elapsed)") { PercentComplete = -1 });
                        nextProgress = now.AddSeconds(1);
                    }

                    Thread.Sleep(200);
                }

                entities = task.GetAwaiter().GetResult();
            }
            finally
            {
                if (adapter is HaloEntityAdapter completedHaloAdapter)
                {
                    completedHaloAdapter.Trace = null;
                    completedHaloAdapter.Progress = null;
                }

                if (adapter is NetSuiteEntityAdapter completedNetSuiteAdapter) completedNetSuiteAdapter.Trace = null;
                if (adapter is NCentralEntityAdapter completedNCentralAdapter) completedNCentralAdapter.Trace = null;
            }
            DrainMessages(traces, progress);
            WriteProgress(new ProgressRecord(1, "Get EntitySync entities", "Complete") { RecordType = ProgressRecordType.Completed });
            foreach (var entity in entities) WriteObject(entity);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "GetEntitySyncEntityFailed", ErrorCategory.ReadError, Vendor));
        }
    }

    private void DrainMessages(ConcurrentQueue<string> traces, ConcurrentQueue<EntitySyncProgress> progress)
    {
        while (traces.TryDequeue(out var trace)) WriteVerbose(trace);
        while (progress.TryDequeue(out var update))
        {
            WriteProgress(new ProgressRecord(1, update.Activity, update.Status) { PercentComplete = update.PercentComplete });
        }
    }

    private void AddEntityTypeParameter(params string[] validValues)
    {
        if (dynamicParameters == null) return;
        var attributes = new Collection<Attribute>
        {
            new ParameterAttribute { Position = 1 },
            new AliasAttribute("Type"),
            new ValidateSetAttribute(validValues)
        };
        dynamicParameters.Add("EntityType", new RuntimeDefinedParameter("EntityType", typeof(string), attributes) { Value = validValues[0] });
    }

    private T DynamicValue<T>(string name, T defaultValue)
    {
        if (dynamicParameters != null && dynamicParameters.TryGetValue(name, out var parameter) && parameter.Value is T value)
        {
            return value;
        }

        return defaultValue;
    }
}
