using System.Net.Http.Headers;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.Halo;

public sealed class HaloEntityAdapter : IEntityAdapter, IDisposable
{
    private const int DefaultPageSize = 100;
    private static readonly string[] PageParameters = { "page_no", "page", "pageno", "pageNumber", "page_number" };

    private readonly HaloOptions options;
    private readonly HttpClient httpClient;

    public HaloEntityAdapter(HaloOptions options)
    {
        this.options = options;
        httpClient = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl)) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => "HaloPSA";

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("HaloPSA adapter currently supports EntityType Client.");
        var entities = new List<ExternalEntity>();
        var requestedTotal = query.Count;
        var pageSize = Math.Min(requestedTotal.GetValueOrDefault(DefaultPageSize), DefaultPageSize);
        var pageNumber = 1;
        PageRequest? pageRequest = null;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!requestedTotal.HasValue || entities.Count < requestedTotal.Value)
        {
            using var document = await GetClientListPageAsync(query, pageSize, pageNumber, pageRequest, seenIds, cancellationToken).ConfigureAwait(false);
            pageRequest ??= document.PageRequest;
            var root = document.Json.RootElement;
            var clients = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("clients", out var array) ? array : root;
            if (clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() == 0) break;

            var addedFromPage = 0;
            foreach (var item in clients.EnumerateArray())
            {
                var entity = MapClient(item);
                if (!string.IsNullOrWhiteSpace(entity.Id) && !seenIds.Add(entity.Id)) continue;
                if (!string.IsNullOrWhiteSpace(entity.Id) && (query.FullObjects || IsAddressEmpty(entity.BillingAddress))) entity = await GetFullClientAsync(entity.Id, cancellationToken).ConfigureAwait(false);
                entities.Add(entity);
                addedFromPage++;
                if (requestedTotal.HasValue && entities.Count >= requestedTotal.Value) break;
            }

            if (addedFromPage == 0) break;
            if (clients.GetArrayLength() < pageSize) break;
            pageNumber++;
        }

        return entities;
    }

    private async Task<ClientListPage> GetClientListPageAsync(EntityQuery query, int pageSize, int pageNumber, PageRequest? pageRequest, HashSet<string> seenIds, CancellationToken cancellationToken)
    {
        var candidates = pageRequest != null
            ? new[] { pageRequest.WithPageNumber(pageNumber) }
            : pageNumber <= 1
                ? new[] { new PageRequest(PageParameters[0], pageNumber, false) }
                : PageParameters.SelectMany(parameter => new[] { new PageRequest(parameter, pageNumber, false), new PageRequest(parameter, pageNumber - 1, true) }).ToArray();

        ClientListPage? duplicatePage = null;
        foreach (var candidate in candidates)
        {
            var page = await FetchClientListPageAsync(BuildClientListUrl(query, pageSize, candidate), candidate, cancellationToken).ConfigureAwait(false);
            if (pageNumber <= 1 || PageHasNewIds(page.Json.RootElement, seenIds)) return page;
            duplicatePage ??= page;
        }

        var fallback = new PageRequest(PageParameters[0], pageNumber, false);
        return duplicatePage ?? await FetchClientListPageAsync(BuildClientListUrl(query, pageSize, fallback), fallback, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClientListPage> FetchClientListPageAsync(string url, PageRequest pageRequest, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ClientListPage(document, pageRequest);
    }

    private static bool PageHasNewIds(JsonElement root, HashSet<string> seenIds)
    {
        var clients = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("clients", out var array) ? array : root;
        if (clients.ValueKind != JsonValueKind.Array) return true;
        foreach (var item in clients.EnumerateArray())
        {
            var id = item.GetString("id", "client_id");
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Contains(id)) return true;
        }

        return false;
    }

    private string BuildClientListUrl(EntityQuery query, int pageSize, PageRequest pageRequest)
    {
        var url = new StringBuilder("api/client?includeinactive=").Append(query.IncludeInactive ? "true" : "false");
        url.Append("&toplevel_id=").Append(options.TopLevelId);
        url.Append("&count=").Append(pageSize);
        url.Append('&').Append(pageRequest.Parameter).Append('=').Append(pageRequest.Value);
        if (!string.IsNullOrWhiteSpace(query.Search)) url.Append("&search=").Append(Uri.EscapeDataString(query.Search));
        return url.ToString();
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new[] { ToHaloClientPayload(request, false) });
        using var response = await httpClient.PostAsync("api/client", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Action = "Create", Success = response.IsSuccessStatusCode, Message = response.IsSuccessStatusCode ? null : text, Raw = text };
    }

    public async Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new[] { ToHaloClientPayload(request, true) });
        using var response = await httpClient.PostAsync("api/client", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Id = request.Id, Action = "Update", Success = response.IsSuccessStatusCode, Message = response.IsSuccessStatusCode ? null : text, Raw = text };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/client?count=1", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<ExternalEntity> GetFullClientAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/client/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("clients", out var clients) && clients.ValueKind == JsonValueKind.Array && clients.GetArrayLength() > 0)
        {
            return MapClient(clients[0]);
        }
        return MapClient(root);
    }

    private ExternalEntity MapClient(JsonElement item)
    {
        var entity = new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Client",
            Id = item.GetString("id", "client_id") ?? string.Empty,
            Name = item.GetString("name", "client_name") ?? string.Empty,
            Email = item.GetString("emailaddress", "email_address", "email", "mainemail", "main_email"),
            Phone = item.GetString("phonenumber", "phone_number", "telephone", "telephone_number", "phone"),
            Website = item.GetString("website", "web_site", "url"),
            Domain = EntityNormalizer.NormalizeDomain(item.GetString("website", "web_site", "url"), item.GetString("emailaddress", "email_address", "email", "mainemail", "main_email")),
            IsActive = item.GetBool("active", "isactive") ?? (item.GetBool("inactive", "isinactive") is bool inactive ? !inactive : null),
            BillingAddress = MapAddress(item),
            Raw = JsonToPsObject(item)
        };

        if (item.TryGetPropertyIgnoreCase("customfields", out var customFields) && customFields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in customFields.EnumerateArray())
            {
                var name = field.GetString("name", "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var value = field.GetString("value", "Value");
                entity.CustomFields[name] = value;
                if (name.Equals(options.NetSuiteCustomerIdField, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    entity.ExternalIds["NetSuiteInternalId"] = value;
                }
            }
        }

        return entity;
    }

    private static EntityAddress MapAddress(JsonElement item)
    {
        return new EntityAddress
        {
            Line1 = ReadAddressString(item, "line1", "address1", "addr1", "address_1", "addressline1", "address_line1", "invoice_address_line1", "invoiceaddress1"),
            Line2 = ReadAddressString(item, "line2", "address2", "addr2", "address_2", "addressline2", "address_line2", "invoice_address_line2", "invoiceaddress2"),
            Line3 = ReadAddressString(item, "line3", "address3", "addr3", "address_3", "addressline3", "address_line3", "invoice_address_line3", "invoiceaddress3"),
            City = ReadAddressString(item, "city", "town", "line4", "address4", "addr4", "address_4", "addressline4", "address_line4", "invoice_address_line4", "invoiceaddress4"),
            State = ReadAddressString(item, "state", "county", "province", "region", "line5", "address5", "addr5", "address_5", "invoice_address_line5", "invoiceaddress5"),
            PostalCode = ReadAddressString(item, "postcode", "postalcode", "postal_code", "zip", "zipcode", "zip_code", "invoice_address_postcode", "invoiceaddresspostcode"),
            Country = ReadAddressString(item, "country", "country_name", "invoice_address_country", "invoiceaddresscountry")
        };
    }

    private static bool IsAddressEmpty(EntityAddress? address)
    {
        return address == null || string.IsNullOrWhiteSpace(address.Compact());
    }

    private static PSObject JsonToPsObject(JsonElement item)
    {
        return PSObject.AsPSObject(JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText()) ?? new Dictionary<string, object?>());
    }

    private static string? ReadAddressString(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var nested = ReadNestedString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
            var flat = item.GetString(propertyName);
            if (!string.IsNullOrWhiteSpace(flat)) return flat;
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement item, string propertyName)
    {
        foreach (var objectName in new[] { "invoice_address", "invoiceaddress", "invoiceAddress", "address", "addresses", "sites" })
        {
            if (!item.TryGetPropertyIgnoreCase(objectName, out var nested)) continue;
            if (nested.ValueKind == JsonValueKind.Object)
            {
                var value = nested.GetString(propertyName);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            if (nested.ValueKind == JsonValueKind.Array)
            {
                foreach (var nestedItem in nested.EnumerateArray())
                {
                    if (nestedItem.ValueKind != JsonValueKind.Object) continue;
                    var value = nestedItem.GetString(propertyName);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        return null;
    }

    private Dictionary<string, object?> ToHaloClientPayload(EntityWriteRequest request, bool includeId)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (includeId) payload["id"] = request.Id;
        payload["name"] = request.Name;
        payload["toplevel_id"] = options.TopLevelId;
        payload["colour"] = options.DefaultColour;
        foreach (var field in request.Fields) payload[field.Key] = field.Value;
        if (request.CustomFields.Count > 0)
        {
            payload["customfields"] = request.CustomFields.Select(field => new Dictionary<string, object?> { ["name"] = field.Key, ["value"] = field.Value }).ToArray();
        }
        return payload;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private sealed class ClientListPage : IDisposable
    {
        public ClientListPage(JsonDocument json, PageRequest pageRequest)
        {
            Json = json;
            PageRequest = pageRequest;
        }

        public JsonDocument Json { get; }
        public PageRequest PageRequest { get; }

        public void Dispose() => Json.Dispose();
    }

    private sealed record PageRequest(string Parameter, int Value, bool ZeroBased)
    {
        public PageRequest WithPageNumber(int pageNumber)
        {
            return this with { Value = ZeroBased ? pageNumber - 1 : pageNumber };
        }
    }
}
