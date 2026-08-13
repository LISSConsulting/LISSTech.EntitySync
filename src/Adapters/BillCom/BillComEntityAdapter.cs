using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.BillCom;

public sealed class BillComEntityAdapter : IEntityAdapter, IDisposable
{
    public const string ClientExternalIdName = "BillSpendClientId";
    public const string ClientUuidExternalIdName = "BillSpendUuid";
    public const string HaloClientCustomFieldName = "CFBillSpendClientID";
    private const int MaximumPagesPerQuery = 100;

    private readonly BillComOptions options;
    private readonly HttpClient httpClient;
    private readonly RateLimitedHttpRequester rateLimiter = new("Bill.com");
    private BillComCustomField? clientFieldCache;

    public BillComEntityAdapter(BillComOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiToken)) throw new ArgumentException("Bill.com API token is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("Bill.com base URL is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientFieldName)) throw new ArgumentException("Bill.com client custom field name is required.", nameof(options));

        httpClient = VendorHttpClientFactory.Create(new Uri(UrlHelpers.EnsureTrailingSlash(options.BaseUrl)));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Add("ApiToken", options.ApiToken);
    }

    public string Vendor => EntitySyncVendors.BillCom;

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Bill.com adapter currently supports EntityType Client.");
        var clientField = await GetClientFieldAsync(cancellationToken).ConfigureAwait(false);
        var entities = new List<ExternalEntity>();
        var nextPage = string.Empty;
        var requestedTotal = query.Count;
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        var pagesRead = 0;

        do
        {
            if (++pagesRead > MaximumPagesPerQuery)
                throw new InvalidOperationException($"Bill.com query exceeded the {MaximumPagesPerQuery}-page scan limit.");
            if (!visitedPages.Add(nextPage))
                throw new InvalidOperationException("Bill.com query returned a repeated continuation token.");
            var pageSize = Math.Min(requestedTotal.GetValueOrDefault(100), 100);
            using var document = await GetValuesPageAsync(clientField.Id, pageSize, nextPage, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetPropertyIgnoreCase("results", out var results) || results.ValueKind != JsonValueKind.Array) break;
            foreach (var item in results.EnumerateArray())
            {
                var entity = MapClientValue(item);
                if (!query.IncludeInactive && entity.IsActive == false) continue;
                if (!MatchesQuery(entity, query)) continue;
                entities.Add(entity);
                if (requestedTotal.HasValue && entities.Count >= requestedTotal.Value) return entities;
            }

            var previousPage = nextPage;
            nextPage = root.GetString("nextPage") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nextPage) && nextPage.Equals(previousPage, StringComparison.Ordinal))
                throw new InvalidOperationException("Bill.com query returned a repeated continuation token.");
        }
        while (!string.IsNullOrWhiteSpace(nextPage));

        return entities;
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (!request.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Bill.com adapter currently supports creating EntityType Client.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Bill.com client name is required.");
        var existing = await FindClientByNameAsync(request.Name, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return new EntityWriteResult { Vendor = Vendor, EntityType = "Client", Id = existing.Id, Action = "Create", Success = true, Message = $"Bill.com client '{request.Name}' already exists.", Raw = existing };
        }

        var clientField = await GetClientFieldAsync(cancellationToken).ConfigureAwait(false);
        using var document = await PostClientValuesAsync(clientField.Id, new[] { request.Name }, cancellationToken).ConfigureAwait(false);
        var created = ReadFirstClient(document.RootElement) ?? new ExternalEntity { Vendor = Vendor, EntityType = "Client", Name = request.Name };
        return new EntityWriteResult { Vendor = Vendor, EntityType = "Client", Id = created.Id, Action = "Create", Success = true, Message = $"Created Bill.com client '{request.Name}'.", Raw = document.RootElement.Clone() };
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (!request.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Bill.com adapter currently supports EntityType Client.");
        if (string.IsNullOrWhiteSpace(request.Id)) return CreateEntityAsync(request, cancellationToken);
        return Task.FromResult(new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "Client",
            Id = request.Id,
            Action = "Update",
            Success = true,
            Message = "Bill.com client custom-field values are already present; the Bill Spend API surface used by EntitySync supports creating values but not updating existing values."
        });
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        _ = await GetClientFieldAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        rateLimiter.Dispose();
        httpClient.Dispose();
    }

    private async Task<BillComCustomField> GetClientFieldAsync(CancellationToken cancellationToken)
    {
        if (clientFieldCache != null) return clientFieldCache;
        Trace?.Invoke("Bill.com GET custom fields");
        using var response = await rateLimiter.SendAsync(httpClient, () => new HttpRequestMessage(HttpMethod.Get, string.Empty), Trace, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonResponseAsync(response, "custom fields", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetPropertyIgnoreCase("results", out var results) || results.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Bill.com custom fields response did not include a results array.");
        foreach (var item in results.EnumerateArray())
        {
            var name = item.GetString("name");
            if (!options.ClientFieldName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var id = item.GetString("id", "Id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"Bill.com custom field '{options.ClientFieldName}' did not include an id.");
            clientFieldCache = new BillComCustomField(id, name ?? options.ClientFieldName);
            return clientFieldCache;
        }

        throw new InvalidOperationException($"Bill.com custom field '{options.ClientFieldName}' was not found.");
    }

    private async Task<JsonDocument> GetValuesPageAsync(string fieldId, int pageSize, string nextPage, CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(fieldId) + "/values?max=" + Math.Max(1, Math.Min(pageSize, 100));
        if (!string.IsNullOrWhiteSpace(nextPage)) path += "&nextPage=" + Uri.EscapeDataString(nextPage);
        Trace?.Invoke("Bill.com GET " + path);
        using var response = await rateLimiter.SendAsync(httpClient, () => new HttpRequestMessage(HttpMethod.Get, path), Trace, cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync(response, "client values", cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostClientValuesAsync(string fieldId, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(fieldId) + "/values";
        var body = JsonSerializer.Serialize(new { values });
        Trace?.Invoke("Bill.com POST " + path);
        using var response = await rateLimiter.SendAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            Trace,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync(response, "create client value", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExternalEntity?> FindClientByNameAsync(string name, CancellationToken cancellationToken)
    {
        var entities = await GetEntitiesAsync(new EntityQuery { EntityType = "Client", IncludeInactive = true }, cancellationToken).ConfigureAwait(false);
        return entities.FirstOrDefault(entity => entity.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesQuery(ExternalEntity entity, EntityQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Search)) return true;
        var search = query.Search.Trim();
        return entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entity.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entity.ExternalIds.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static ExternalEntity MapClientValue(JsonElement item)
    {
        var rawId = FirstNonEmpty(item.GetString("id", "Id"), item.GetString("uuid")) ?? string.Empty;
        var id = DecodeBillComValueId(rawId);
        var uuid = item.GetString("uuid") ?? string.Empty;
        var name = item.GetString("value", "name") ?? string.Empty;
        var deleted = item.GetBool("deleted") ?? false;
        var entity = new ExternalEntity
        {
            Vendor = EntitySyncVendors.BillCom,
            EntityType = "Client",
            Id = id,
            Name = name,
            IsActive = !deleted
        };
        if (!string.IsNullOrWhiteSpace(id))
        {
            entity.ExternalIds[ClientExternalIdName] = id;
            entity.CustomFields[ClientExternalIdName] = id;
        }
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            entity.ExternalIds[ClientUuidExternalIdName] = uuid;
            entity.CustomFields[ClientUuidExternalIdName] = uuid;
        }
        entity.CustomFields["BillSpendDeleted"] = deleted.ToString();
        return entity;
    }

    internal static string DecodeBillComValueId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return string.Empty;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rawId));
            var colonIndex = decoded.LastIndexOf(':');
            return colonIndex >= 0 && colonIndex < decoded.Length - 1 ? decoded[(colonIndex + 1)..] : rawId;
        }
        catch (FormatException)
        {
            return rawId;
        }
    }

    private static ExternalEntity? ReadFirstClient(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray()) return MapClientValue(item);
        }

        return null;
    }

    private static async Task<JsonDocument> ReadJsonResponseAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Bill.com {operation} request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response preview: {Preview(text)}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Bill.com {operation} request returned non-JSON content. Response preview: {Preview(text)}", ex);
        }
    }

    private static string Preview(string text)
    {
        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "...";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private sealed record BillComCustomField(string Id, string Name);
}
