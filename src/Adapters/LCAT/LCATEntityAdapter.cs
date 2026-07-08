using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.LCAT;

public sealed class LCATEntityAdapter : IEntityAdapter, IDisposable
{
    private readonly LCATOptions options;
    private readonly HttpClient httpClient = new();

    public LCATEntityAdapter(LCATOptions options)
    {
        this.options = options;
    }

    public string Vendor => "LCAT";

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException("LCAT Customer reads are implemented in a later EntitySync task.");
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LCAT does not support per-item create. Apply an approved plan through the LCAT batch sync path.");
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LCAT does not support per-item update. Apply an approved plan through the LCAT batch sync path.");
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException("LCAT connection testing is implemented in a later EntitySync task.");
    }

    public void Dispose() => httpClient.Dispose();
}
