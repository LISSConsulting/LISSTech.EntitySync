using System.Net.Http.Headers;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.LCAT;

public sealed class LCATCustomerScopeRequest
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NCentralCustomerId { get; set; } = string.Empty;
    public string? NCentralParentCustomerId { get; set; }
}

public sealed class LCATSyncResult
{
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RetiredCount { get; set; }
    public int ActiveCount { get; set; }
    public string? AuditEventId { get; set; }
}

public sealed class LCATEntityAdapter : IEntityAdapter, IDisposable
{
    private const string SyncReason = "EntitySync N-central to LCAT sync";

    private readonly LCATOptions options;
    private readonly HttpClient httpClient = new();

    public LCATEntityAdapter(LCATOptions options)
    {
        this.options = options;
        httpClient.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    }

    public string Vendor => "LCAT";

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("LCAT adapter currently supports EntityType Customer.");

        // No LCAT customer-scope list/read endpoint is defined in the sync RPC contract
        // (contracts/lcat-sync-rpc.md); returning an empty set lets N-central sources plan as
        // create/sync candidates per contracts/powershell-command-contract.md.
        return Task.FromResult<IReadOnlyList<ExternalEntity>>(Array.Empty<ExternalEntity>());
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

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => httpClient.Dispose();

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private static string BuildSyncRequestBody(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["customers"] = customers.Select(customer => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = customer.Slug,
                ["display_name"] = customer.DisplayName,
                ["ncentral_customer_id"] = customer.NCentralCustomerId,
                ["ncentral_parent_customer_id"] = customer.NCentralParentCustomerId
            }).ToArray(),
            ["reason"] = SyncReason,
            ["ticket"] = null
        };

        return JsonSerializer.Serialize(payload);
    }

    private static LCATSyncResult ParseSyncResponse(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        return new LCATSyncResult
        {
            InsertedCount = root.GetInt("inserted_count") ?? 0,
            UpdatedCount = root.GetInt("updated_count") ?? 0,
            RetiredCount = root.GetInt("retired_count") ?? 0,
            ActiveCount = root.GetInt("active_count") ?? 0,
            AuditEventId = root.GetString("audit_event_id")
        };
    }
}
