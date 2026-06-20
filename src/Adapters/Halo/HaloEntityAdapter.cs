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
    private const int DefaultEnrichmentConcurrency = 8;

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

    public Action<string>? Trace { get; set; }
    public Action<EntitySyncProgress>? Progress { get; set; }

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("HaloPSA adapter currently supports EntityType Client.");
        var entities = new List<ExternalEntity>();
        var requestedTotal = query.Count;
        var pageSize = Math.Min(requestedTotal.GetValueOrDefault(DefaultPageSize), DefaultPageSize);
        var pageNumber = 1;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!requestedTotal.HasValue || entities.Count < requestedTotal.Value)
        {
            Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA clients", Status = $"Reading page {pageNumber}" });
            using var document = await FetchClientListPageAsync(BuildClientListUrl(query, pageSize, pageNumber), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var clients = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("clients", out var array) ? array : root;
            if (clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() == 0) break;

            var pageEntities = new List<ExternalEntity>();
            foreach (var item in clients.EnumerateArray())
            {
                var entity = MapClient(item);
                if (!string.IsNullOrWhiteSpace(entity.Id) && !seenIds.Add(entity.Id)) continue;
                pageEntities.Add(entity);
                if (requestedTotal.HasValue && entities.Count + pageEntities.Count >= requestedTotal.Value) break;
            }

            if (query.FullObjects) pageEntities = await EnrichClientsAsync(pageEntities, entities.Count, cancellationToken).ConfigureAwait(false);
            entities.AddRange(pageEntities);

            if (pageEntities.Count == 0) break;
            if (clients.GetArrayLength() < pageSize) break;
            pageNumber++;
        }

        return entities;
    }

    private async Task<List<ExternalEntity>> EnrichClientsAsync(List<ExternalEntity> clients, int existingCount, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(DefaultEnrichmentConcurrency);
        var tasks = clients.Select((client, index) => EnrichClientAsync(client, existingCount + index + 1, throttle, cancellationToken)).ToArray();
        var enriched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return enriched.ToList();
    }

    private async Task<ExternalEntity> EnrichClientAsync(ExternalEntity client, int ordinal, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Id)) return client;
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA clients", Status = $"Enriching client {ordinal}: {client.Name}" });
            var enriched = await GetFullClientAsync(client.Id, cancellationToken).ConfigureAwait(false);
            var site = await GetMainSiteAsync(enriched.Raw, cancellationToken).ConfigureAwait(false);
            if (site != null) ApplySiteDetails(enriched, site.Value);
            return enriched;
        }
        finally
        {
            throttle.Release();
        }
    }

    public async Task<IReadOnlyList<EntitySyncTopLevel>> GetTopLevelsAsync(CancellationToken cancellationToken)
    {
        Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA top levels", Status = "Reading top-level records" });
        using var response = await httpClient.GetAsync("api/toplevel", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("toplevels", out var array) ? array : root;
        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<EntitySyncTopLevel>();

        var topLevels = new List<EntitySyncTopLevel>();
        foreach (var row in rows.EnumerateArray())
        {
            topLevels.Add(new EntitySyncTopLevel
            {
                Vendor = Vendor,
                Id = row.GetInt("id", "toplevel_id", "key") ?? 0,
                Name = row.GetString("name", "toplevel_name") ?? string.Empty,
                Raw = JsonToPsObject(row)
            });
        }

        return topLevels;
    }

    private async Task<JsonDocument> FetchClientListPageAsync(string url, CancellationToken cancellationToken)
    {
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string BuildClientListUrl(EntityQuery query, int pageSize, int pageNumber)
    {
        var url = new StringBuilder("api/client?includeinactive=").Append(query.IncludeInactive ? "true" : "false");
        url.Append("&toplevel_id=").Append(options.TopLevelId);
        url.Append("&pageinate=true");
        url.Append("&page_size=").Append(pageSize);
        url.Append("&page_no=").Append(pageNumber);
        url.Append("&include_website=true");
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

    private async Task<JsonElement?> GetMainSiteAsync(PSObject? raw, CancellationToken cancellationToken)
    {
        var siteId = raw?.Properties["main_site_id"]?.Value?.ToString();
        var clientId = raw?.Properties["id"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(siteId) || siteId == "0") return null;

        var url = "api/Site/" + Uri.EscapeDataString(siteId) + "?includedetails=true";
        if (!string.IsNullOrWhiteSpace(clientId)) url += "&client_override=" + Uri.EscapeDataString(clientId);
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Trace?.Invoke($"HaloPSA site lookup for site {siteId} failed with HTTP {(int)response.StatusCode}");
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("sites", out var sites) && sites.ValueKind == JsonValueKind.Array && sites.GetArrayLength() > 0)
        {
            return sites[0].Clone();
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("site", out var site) && site.ValueKind == JsonValueKind.Object)
        {
            return site.Clone();
        }

        return root.Clone();
    }

    private static void ApplySiteDetails(ExternalEntity entity, JsonElement site)
    {
        entity.PrimarySiteRaw = JsonToPsObject(site);
        entity.PrimarySiteId ??= site.GetString("id", "site_id", "key");
        entity.PrimarySiteName ??= site.GetString("name", "site_name");
        if (IsAddressEmpty(entity.BillingAddress)) entity.BillingAddress = MapAddress(site);
        if (string.IsNullOrWhiteSpace(entity.Email)) entity.Email = site.GetString("accountsemailaddress", "accounts_email_address", "emailaddress", "email_address", "email", "mainemail", "main_email");
        if (string.IsNullOrWhiteSpace(entity.Phone)) entity.Phone = site.GetString("phonenumber", "phone_number", "telephone", "telephone_number", "phone", "mainphone", "main_phone", "tel");
        if (string.IsNullOrWhiteSpace(entity.Website)) entity.Website = site.GetString("website", "web_site", "url");
        entity.Domain = EntityNormalizer.NormalizeDomain(entity.Website, entity.Email);
    }

    private ExternalEntity MapClient(JsonElement item)
    {
        var entity = new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Client",
            Id = item.GetString("id", "client_id") ?? string.Empty,
            Name = item.GetString("name", "client_name") ?? string.Empty,
            Email = item.GetString("accountsemailaddress", "accounts_email_address", "emailaddress", "email_address", "email", "mainemail", "main_email"),
            Phone = item.GetString("phonenumber", "phone_number", "telephone", "telephone_number", "phone", "mainphone", "main_phone", "tel"),
            Website = item.GetString("website", "web_site", "url"),
            Domain = EntityNormalizer.NormalizeDomain(item.GetString("website", "web_site", "url"), item.GetString("accountsemailaddress", "accounts_email_address", "emailaddress", "email_address", "email", "mainemail", "main_email")),
            PrimarySiteId = item.GetString("main_site_id"),
            PrimarySiteName = item.GetString("main_site_name"),
            IsActive = item.GetBool("active", "isactive") ?? (item.GetBool("inactive", "isinactive") is bool inactive ? !inactive : null),
            BillingAddress = MapAddress(item),
            CreatedAt = item.GetDate("datecreated", "created_at", "created"),
            UpdatedAt = item.GetDate("alastupdate", "last_update", "updated_at", "updated"),
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
        if (TryGetAddressObject(item, out var address)) item = address;
        return new EntityAddress
        {
            Line1 = ReadAddressString(item, "line1", "address1", "addr1", "address_1", "addressline1", "address_line1", "delivery_address_line1", "deliveryaddress1", "invoice_address_line1", "invoiceaddress1"),
            Line2 = ReadAddressString(item, "line2", "address2", "addr2", "address_2", "addressline2", "address_line2", "delivery_address_line2", "deliveryaddress2", "invoice_address_line2", "invoiceaddress2"),
            Line3 = ReadAddressString(item, "line3", "address3", "addr3", "address_3", "addressline3", "address_line3", "delivery_address_line3", "deliveryaddress3", "invoice_address_line3", "invoiceaddress3"),
            City = ReadAddressString(item, "city", "town", "line4", "address4", "addr4", "address_4", "addressline4", "address_line4", "delivery_address_line4", "deliveryaddress4", "invoice_address_line4", "invoiceaddress4"),
            State = ReadAddressString(item, "state", "county", "province", "region", "line5", "address5", "addr5", "address_5", "delivery_address_line5", "deliveryaddress5", "invoice_address_line5", "invoiceaddress5"),
            PostalCode = ReadAddressString(item, "postcode", "postalcode", "postal_code", "zip", "zipcode", "zip_code", "delivery_address_postcode", "deliveryaddresspostcode", "invoice_address_postcode", "invoiceaddresspostcode"),
            Country = ReadAddressString(item, "country", "country_name", "delivery_address_country", "deliveryaddresscountry", "invoice_address_country", "invoiceaddresscountry")
        };
    }

    private static bool TryGetAddressObject(JsonElement item, out JsonElement address)
    {
        foreach (var objectName in new[] { "delivery_address", "deliveryaddress", "deliveryAddress", "invoice_address", "invoiceaddress", "invoiceAddress", "address" })
        {
            if (item.TryGetPropertyIgnoreCase(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                address = nested;
                return true;
            }
        }

        address = default;
        return false;
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
        foreach (var objectName in new[] { "delivery_address", "deliveryaddress", "deliveryAddress", "invoice_address", "invoiceaddress", "invoiceAddress", "address", "addresses", "sites" })
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

}
