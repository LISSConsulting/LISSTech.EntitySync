using System.Collections.ObjectModel;
using System.Management.Automation;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.Get, "EntitySyncEntity")]
[OutputType(typeof(ExternalEntity))]
public sealed class GetEntitySyncEntityCommand : PSCmdlet, IDynamicParameters
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string Vendor { get; set; } = string.Empty;

    private RuntimeDefinedParameterDictionary? dynamicParameters;

    public object? GetDynamicParameters()
    {
        dynamicParameters = new RuntimeDefinedParameterDictionary();
        if (Vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase))
        {
            AddEntityTypeParameter("Client");
        }
        else if (Vendor.Equals("NetSuite", StringComparison.OrdinalIgnoreCase))
        {
            AddEntityTypeParameter("Customer");
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

    protected override void EndProcessing()
    {
        try
        {
            var entityType = DynamicValue<string?>("EntityType", null) ?? throw new InvalidOperationException("EntityType is required.");
            var query = new EntityQuery { EntityType = entityType, Search = Search, IncludeInactive = IncludeInactive, FullObjects = FullObjects };
            if (Count > 0) query.Count = Count;
            var adapter = ConnectionRegistry.Get(Vendor);
            if (adapter is HaloEntityAdapter haloAdapter) haloAdapter.Trace = WriteVerbose;
            var entities = adapter.GetEntitiesAsync(query, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var entity in entities) WriteObject(entity);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "GetEntitySyncEntityFailed", ErrorCategory.ReadError, Vendor));
        }
    }

    private void AddEntityTypeParameter(params string[] validValues)
    {
        if (dynamicParameters == null) return;
        var attributes = new Collection<Attribute>
        {
            new ParameterAttribute { Mandatory = true, Position = 1 },
            new AliasAttribute("Type"),
            new ValidateSetAttribute(validValues)
        };
        dynamicParameters.Add("EntityType", new RuntimeDefinedParameter("EntityType", typeof(string), attributes));
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
