using System.Net.Http.Headers;
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
    private const int CountryLookupId = 74;
    private const int RegionLookupId = 77;

    private readonly HaloOptions options;
    private readonly HttpClient httpClient;
    private IReadOnlyList<LookupOption>? countryLookupCache;
    private readonly Dictionary<string, IReadOnlyList<LookupOption>> regionLookupCache = new(StringComparer.OrdinalIgnoreCase);

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
            var site = await GetMainSiteAsync(enriched.PrimarySiteId, enriched.Id, cancellationToken).ConfigureAwait(false);
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
                Name = row.GetString("name", "toplevel_name") ?? string.Empty
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
        if (!response.IsSuccessStatusCode)
        {
            return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Id = request.Id, Action = "Update", Success = false, Message = text, Raw = text };
        }

        var siteResult = await UpdatePrimarySiteAsync(request, cancellationToken).ConfigureAwait(false);
        if (siteResult != null)
        {
            return new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = request.Id,
                Action = "Update",
                Success = siteResult.Success,
                Message = siteResult.Success ? "Client and primary site updated." : siteResult.Message,
                Raw = new { Client = text, PrimarySite = siteResult.Raw }
            };
        }

        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Id = request.Id, Action = "Update", Success = true, Raw = text };
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

    private async Task<JsonElement?> GetMainSiteAsync(string? siteId, string? clientId, CancellationToken cancellationToken)
    {
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
        entity.PrimarySiteId ??= site.GetString("id", "site_id", "key");
        entity.PrimarySiteName ??= site.GetString("name", "site_name");
        if (IsAddressEmpty(entity.PrimaryAddress)) entity.PrimaryAddress = MapAddress(site);
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
            PrimaryAddress = MapAddress(item),
            CreatedAt = item.GetDate("datecreated", "created_at", "created"),
            UpdatedAt = item.GetDate("alastupdate", "last_update", "updated_at", "updated")
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
        foreach (var field in request.Fields)
        {
            if (includeId && IsPrimarySiteField(field.Key)) continue;
            payload[field.Key] = field.Value;
        }
        if (request.CustomFields.Count > 0)
        {
            payload["customfields"] = request.CustomFields.Select(field => new Dictionary<string, object?> { ["name"] = field.Key, ["value"] = field.Value }).ToArray();
        }
        return payload;
    }

    private static bool IsPrimarySiteField(string fieldName)
    {
        return fieldName.Equals("clientsite_name", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("phonenumber", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("delivery_address", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EntityWriteResult?> UpdatePrimarySiteAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var siteId = request.PrimarySiteId;
        if (string.IsNullOrWhiteSpace(siteId) || siteId == "0")
        {
            var client = !string.IsNullOrWhiteSpace(request.Id) ? await GetFullClientAsync(request.Id, cancellationToken).ConfigureAwait(false) : null;
            siteId = client?.PrimarySiteId;
        }

        if (string.IsNullOrWhiteSpace(siteId) || siteId == "0") return null;

        var sitePayload = await ToHaloPrimarySitePayloadAsync(request, siteId, cancellationToken).ConfigureAwait(false);
        if (sitePayload.Count <= 2) return null;

        var body = JsonSerializer.Serialize(new[] { sitePayload });
        Trace?.Invoke("HaloPSA POST api/Site");
        using var response = await httpClient.PostAsync("api/Site", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = request.EntityType,
            Id = request.Id,
            Action = "UpdatePrimarySite",
            Success = response.IsSuccessStatusCode,
            Message = response.IsSuccessStatusCode ? null : text,
            Raw = text
        };
    }

    private async Task<Dictionary<string, object?>> ToHaloPrimarySitePayloadAsync(EntityWriteRequest request, string siteId, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ToHaloIdValue(siteId),
            ["client_id"] = ToHaloIdValue(request.Id)
        };

        if (request.Fields.TryGetValue("phonenumber", out var phone) && phone != null) payload["phonenumber"] = phone;
        if (!request.Fields.TryGetValue("delivery_address", out var addressValue) || addressValue is not Dictionary<string, object?> address) return payload;

        payload["delivery_address"] = address;

        var siteAddress = new EntityAddress
        {
            Line1 = address.TryGetValue("line1", out var line1) ? line1?.ToString() : null,
            Line2 = address.TryGetValue("line2", out var line2) ? line2?.ToString() : null,
            City = address.TryGetValue("line3", out var city) ? city?.ToString() : null,
            State = address.TryGetValue("line4", out var state) ? state?.ToString() : null,
            PostalCode = address.TryGetValue("postcode", out var postcode) ? postcode?.ToString() : null,
            Country = address.TryGetValue("country", out var countryValue) ? countryValue?.ToString() : null
        };

        var countryLookup = await ResolveCountryAsync(siteAddress, cancellationToken).ConfigureAwait(false);
        if (countryLookup != null)
        {
            payload["country_code"] = countryLookup.Id.ToString();
            var region = await ResolveRegionAsync(siteAddress, countryLookup.Id, cancellationToken).ConfigureAwait(false);
            if (region != null) payload["region_code"] = region.Id;
        }

        var timezone = InferWindowsTimeZone(siteAddress);
        if (!string.IsNullOrWhiteSpace(timezone)) payload["timezone"] = timezone;

        return payload;
    }

    private async Task<LookupOption?> ResolveCountryAsync(EntityAddress address, CancellationToken cancellationToken)
    {
        var terms = CountryTerms(address).ToArray();
        if (terms.Length == 0) return null;
        var countries = countryLookupCache ??= await GetLookupAsync(CountryLookupId, null, cancellationToken).ConfigureAwait(false);
        return FindLookup(countries, terms);
    }

    private async Task<LookupOption?> ResolveRegionAsync(EntityAddress address, int countryId, CancellationToken cancellationToken)
    {
        var terms = RegionTerms(address).ToArray();
        if (terms.Length == 0) return null;
        if (!regionLookupCache.TryGetValue(countryId.ToString(), out var regions))
        {
            regions = await GetLookupAsync(RegionLookupId, countryId, cancellationToken).ConfigureAwait(false);
            regionLookupCache[countryId.ToString()] = regions;
        }

        return FindLookup(regions, terms);
    }

    private async Task<IReadOnlyList<LookupOption>> GetLookupAsync(int lookupId, int? countryCodeId, CancellationToken cancellationToken)
    {
        var url = "api/Lookup?lookupid=" + lookupId;
        if (countryCodeId.HasValue) url += "&country_code_id=" + countryCodeId.Value;
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLookupOptions(document.RootElement).ToArray();
    }

    private static IEnumerable<LookupOption> ParseLookupOptions(JsonElement root)
    {
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("lookup", out var lookup) ? lookup : root;
        if (rows.ValueKind == JsonValueKind.Object && rows.TryGetPropertyIgnoreCase("lookups", out var lookups)) rows = lookups;
        if (rows.ValueKind != JsonValueKind.Array) yield break;

        foreach (var row in rows.EnumerateArray())
        {
            var id = row.GetInt("id") ?? 0;
            if (id == 0) continue;
            var values = new List<string?> { row.GetString("name"), row.GetString("originalvalue"), row.GetString("value"), row.GetString("value2"), row.GetString("value3"), row.GetString("value4"), row.GetString("value5"), row.GetString("value6"), row.GetString("value7"), row.GetString("value8"), row.GetString("value9"), row.GetString("value10"), id.ToString() };
            yield return new LookupOption(id, values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray());
        }
    }

    private static LookupOption? FindLookup(IEnumerable<LookupOption> options, IEnumerable<string> terms)
    {
        var normalizedTerms = terms.Select(NormalizeLookupValue).Where(term => term.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (option.Values.Select(NormalizeLookupValue).Any(normalizedTerms.Contains)) return option;
        }

        return null;
    }

    private static IEnumerable<string> CountryTerms(EntityAddress address)
    {
        var country = address.Country?.Trim();
        if (!string.IsNullOrWhiteSpace(country))
        {
            yield return country;
            if (country.Equals("US", StringComparison.OrdinalIgnoreCase) || country.Equals("USA", StringComparison.OrdinalIgnoreCase)) yield return "United States";
            if (country.Equals("CA", StringComparison.OrdinalIgnoreCase)) yield return "Canada";
        }

        if (LooksCanadian(address))
        {
            yield return "Canada";
            yield return "CA";
        }
        else if (LooksUnitedStates(address))
        {
            yield return "United States";
            yield return "United States of America";
            yield return "USA";
            yield return "US";
        }
    }

    private static IEnumerable<string> RegionTerms(EntityAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.State)) yield break;
        var state = address.State.Trim();
        yield return state;
        if (StateNames.TryGetValue(state, out var stateName)) yield return stateName;
        if (StateCodes.TryGetValue(state, out var stateCode)) yield return stateCode;
    }

    private static string? InferWindowsTimeZone(EntityAddress address)
    {
        if (LooksCanadian(address)) return InferCanadianTimeZone(address.State);
        if (LooksUnitedStates(address)) return InferUnitedStatesTimeZone(address.State);
        return null;
    }

    private static string? InferUnitedStatesTimeZone(string? state)
    {
        var code = NormalizeRegionCode(state);
        return code switch
        {
            "ME" or "NH" or "VT" or "MA" or "RI" or "CT" or "NY" or "NJ" or "PA" or "DE" or "MD" or "DC" or "VA" or "WV" or "NC" or "SC" or "GA" or "FL" or "OH" or "MI" or "IN" or "KY" => "Eastern Standard Time",
            "AL" or "AR" or "IL" or "IA" or "LA" or "MN" or "MS" or "MO" or "OK" or "WI" or "TX" or "TN" or "KS" or "NE" or "ND" or "SD" => "Central Standard Time",
            "AZ" or "CO" or "ID" or "MT" or "NM" or "UT" or "WY" => "Mountain Standard Time",
            "CA" or "NV" or "OR" or "WA" => "Pacific Standard Time",
            "AK" => "Alaskan Standard Time",
            "HI" => "Hawaiian Standard Time",
            _ => null
        };
    }

    private static string? InferCanadianTimeZone(string? province)
    {
        var code = NormalizeRegionCode(province);
        return code switch
        {
            "NL" => "Newfoundland Standard Time",
            "NB" or "NS" or "PE" => "Atlantic Standard Time",
            "ON" or "QC" => "Eastern Standard Time",
            "MB" or "SK" => "Central Standard Time",
            "AB" or "NT" or "NU" or "YT" => "Mountain Standard Time",
            "BC" => "Pacific Standard Time",
            _ => null
        };
    }

    private static bool LooksUnitedStates(EntityAddress address)
    {
        var country = address.Country?.Trim();
        if (!string.IsNullOrWhiteSpace(country)) return IsUnitedStates(country);
        if (!string.IsNullOrWhiteSpace(address.State) && NormalizeRegionCode(address.State) is string code && UnitedStatesCodes.Contains(code)) return true;
        return !string.IsNullOrWhiteSpace(address.PostalCode) && address.PostalCode.Trim().Take(5).All(char.IsDigit);
    }

    private static bool LooksCanadian(EntityAddress address)
    {
        var country = address.Country?.Trim();
        if (!string.IsNullOrWhiteSpace(country)) return IsCanada(country);
        if (!string.IsNullOrWhiteSpace(address.State) && NormalizeRegionCode(address.State) is string code && CanadianCodes.Contains(code)) return true;
        return !string.IsNullOrWhiteSpace(address.PostalCode) && address.PostalCode.Any(char.IsLetter);
    }

    private static bool IsUnitedStates(string country) => country.Equals("US", StringComparison.OrdinalIgnoreCase) || country.Equals("USA", StringComparison.OrdinalIgnoreCase) || country.Equals("United States", StringComparison.OrdinalIgnoreCase) || country.Equals("United States of America", StringComparison.OrdinalIgnoreCase);

    private static bool IsCanada(string country) => country.Equals("CA", StringComparison.OrdinalIgnoreCase) || country.Equals("Canada", StringComparison.OrdinalIgnoreCase);

    private static object? ToHaloIdValue(string? value)
    {
        return int.TryParse(value, out var id) ? id : value;
    }

    private static string NormalizeRegionCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (StateCodes.TryGetValue(trimmed, out var code)) return code;
        return trimmed.ToUpperInvariant();
    }

    private static string NormalizeLookupValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private sealed record LookupOption(int Id, IReadOnlyList<string> Values);

    private static readonly Dictionary<string, string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia", ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana", ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada", ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York", ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota", ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia", ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming", ["AB"] = "Alberta", ["BC"] = "British Columbia", ["MB"] = "Manitoba", ["NB"] = "New Brunswick", ["NL"] = "Newfoundland and Labrador", ["NS"] = "Nova Scotia", ["NT"] = "Northwest Territories", ["NU"] = "Nunavut", ["ON"] = "Ontario", ["PE"] = "Prince Edward Island", ["QC"] = "Quebec", ["SK"] = "Saskatchewan", ["YT"] = "Yukon"
    };

    private static readonly HashSet<string> CanadianCodes = new(StringComparer.OrdinalIgnoreCase) { "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT" };
    private static readonly Dictionary<string, string> StateCodes = StateNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnitedStatesCodes = StateNames.Keys.Where(code => code.Length == 2 && !CanadianCodes.Contains(code)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

}
