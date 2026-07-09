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
    private const int DefaultEnrichmentConcurrency = 2;
    private const int CountryLookupId = 74;
    private const int RegionLookupId = 77;
    private const int CustomerRelationshipLookupId = 89;
    private const int CustomerTypeLookupId = 33;

    private readonly HaloOptions options;
    private readonly HttpClient httpClient;
    private readonly RateLimitedHttpRequester rateLimiter = new("HaloPSA");
    private string? accountManagerIdCache;
    private LookupOption? customerRelationshipCache;
    private LookupOption? customerTypeCache;
    private string? netSuiteCustomerIdFieldIdCache;
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

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }
    public Action<EntitySyncProgress>? Progress { get; set; }

    public string NetSuiteCustomerIdField => options.NetSuiteCustomerIdField;
    public string NetSuiteCustomerNameField => options.NetSuiteCustomerNameField;

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (query.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)) return await GetSitesAsync(query, cancellationToken).ConfigureAwait(false);
        if (!query.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("HaloPSA adapter currently supports EntityType Client.");
        var entities = new List<ExternalEntity>();
        var requestedTotal = query.Count;
        var pageSize = Math.Min(requestedTotal.GetValueOrDefault(DefaultPageSize), DefaultPageSize);
        var pageNumber = 1;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedCustomFieldIds = await ResolveIncludedCustomFieldIdsAsync(query.RequiredCustomFieldName, cancellationToken).ConfigureAwait(false);
        var fullObjects = query.FullObjects || (!string.IsNullOrWhiteSpace(query.RequiredCustomFieldName) && string.IsNullOrWhiteSpace(includedCustomFieldIds));
        if (fullObjects && !query.FullObjects && !string.IsNullOrWhiteSpace(query.RequiredCustomFieldName))
        {
            Trace?.Invoke($"HaloPSA custom field '{query.RequiredCustomFieldName}' could not be resolved to an ID for list reads. Falling back to full client records.");
        }

        while (!requestedTotal.HasValue || entities.Count < requestedTotal.Value)
        {
            Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA clients", Status = $"Reading page {pageNumber}" });
            using var document = await FetchClientListPageAsync(BuildClientListUrl(query, pageSize, pageNumber, includedCustomFieldIds), cancellationToken).ConfigureAwait(false);
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

            if (fullObjects) pageEntities = await EnrichClientsAsync(pageEntities, entities.Count, query.IncludeSiteDetails, query.ThrottleLimit, cancellationToken).ConfigureAwait(false);
            entities.AddRange(pageEntities);

            if (pageEntities.Count == 0) break;
            if (clients.GetArrayLength() < pageSize) break;
            pageNumber++;
        }

        return entities;
    }

    private async Task<IReadOnlyList<ExternalEntity>> GetSitesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        var clients = await GetEntitiesAsync(new EntityQuery { EntityType = "Client", IncludeInactive = query.IncludeInactive, ThrottleLimit = query.ThrottleLimit }, cancellationToken).ConfigureAwait(false);
        var sites = new List<ExternalEntity>();
        using var throttle = new SemaphoreSlim(query.ThrottleLimit > 0 ? query.ThrottleLimit : DefaultEnrichmentConcurrency);
        var tasks = clients.Select(async client =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await GetClientSitesAsync(client, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        foreach (var batch in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            foreach (var site in batch)
            {
                if (!MatchesSiteQuery(site, query)) continue;
                sites.Add(site);
                if (query.Count.HasValue && sites.Count >= query.Count.Value) return sites;
            }
        }

        return sites;
    }

    private async Task<IReadOnlyList<ExternalEntity>> GetClientSitesAsync(ExternalEntity client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Id)) return Array.Empty<ExternalEntity>();
        var url = "api/Site?count=1000&client_id=" + Uri.EscapeDataString(client.Id);
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Trace?.Invoke($"HaloPSA site list for client {client.Id} failed with HTTP {(int)response.StatusCode}");
            return Array.Empty<ExternalEntity>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("sites", out var array) ? array : root;
        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<ExternalEntity>();
        return rows.EnumerateArray().Select(site => MapSite(site, client)).ToArray();
    }

    private static bool MatchesSiteQuery(ExternalEntity site, EntityQuery query)
    {
        if (!query.IncludeInactive && site.IsActive == false) return false;
        if (string.IsNullOrWhiteSpace(query.Search)) return true;
        var search = query.Search.Trim();
        return site.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || site.CustomFields.Values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            || site.ExternalIds.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        return await rateLimiter.SendAsync(httpClient, () => new HttpRequestMessage(HttpMethod.Get, url), Trace, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
    {
        return await rateLimiter.SendAsync(httpClient, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, Trace, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ExternalEntity>> EnrichClientsAsync(List<ExternalEntity> clients, int existingCount, bool includeSiteDetails, int throttleLimit, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(throttleLimit > 0 ? throttleLimit : DefaultEnrichmentConcurrency);
        var tasks = clients.Select((client, index) => EnrichClientAsync(client, existingCount + index + 1, includeSiteDetails, throttle, cancellationToken)).ToArray();
        var enriched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return enriched.ToList();
    }

    private async Task<ExternalEntity> EnrichClientAsync(ExternalEntity client, int ordinal, bool includeSiteDetails, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Id)) return client;
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA clients", Status = $"Enriching client {ordinal}: {client.Name}" });
            var enriched = await GetFullClientAsync(client.Id, cancellationToken).ConfigureAwait(false);
            if (!includeSiteDetails) return enriched;

            var site = await GetMainSiteAsync(enriched.PrimarySiteId, enriched.Id, cancellationToken).ConfigureAwait(false);
            if (site != null) ApplySiteDetails(enriched, site.Value);
            return enriched;
        }
        finally
        {
            throttle.Release();
        }
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        if (type.Equals(EntitySyncLookupTypes.TopLevel, StringComparison.OrdinalIgnoreCase)) return GetLookupTopLevelsAsync(cancellationToken);
        if (type.Equals(EntitySyncLookupTypes.CustomerRelationship, StringComparison.OrdinalIgnoreCase)) return GetHaloLookupAsync(EntitySyncLookupTypes.CustomerRelationship, CustomerRelationshipLookupId, cancellationToken);
        if (type.Equals(EntitySyncLookupTypes.CustomerType, StringComparison.OrdinalIgnoreCase)) return GetHaloLookupAsync(EntitySyncLookupTypes.CustomerType, CustomerTypeLookupId, cancellationToken);
        if (type.Equals(EntitySyncLookupTypes.NCentralIntegration, StringComparison.OrdinalIgnoreCase)) return GetNCentralIntegrationsAsync(cancellationToken);
        if (type.Equals(EntitySyncLookupTypes.NCentralIntegrationLink, StringComparison.OrdinalIgnoreCase)) return GetNCentralIntegrationLinksLookupAsync(cancellationToken);
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    private async Task<IReadOnlyList<EntitySyncLookup>> GetHaloLookupAsync(string type, int lookupId, CancellationToken cancellationToken)
    {
        var rows = await GetLookupAsync(lookupId, null, cancellationToken).ConfigureAwait(false);
        return rows.Select(row =>
        {
            var lookup = new EntitySyncLookup
            {
                Vendor = Vendor,
                Type = type,
                Id = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = row.Name
            };
            AddProperty(lookup, "LookupId", lookupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return lookup;
        }).ToArray();
    }

    private async Task<IReadOnlyList<EntitySyncLookup>> GetLookupTopLevelsAsync(CancellationToken cancellationToken)
    {
        Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA top levels", Status = "Reading top-level records" });
        using var response = await GetAsync("api/toplevel", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("toplevels", out var array) ? array : root;
        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<EntitySyncLookup>();

        var topLevels = new List<EntitySyncLookup>();
        foreach (var row in rows.EnumerateArray())
        {
            topLevels.Add(new EntitySyncLookup
            {
                Vendor = Vendor,
                Type = "TopLevel",
                Id = (row.GetInt("id", "toplevel_id", "key") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = row.GetString("name", "toplevel_name") ?? string.Empty
            });
        }

        return topLevels;
    }

    private async Task<IReadOnlyList<EntitySyncLookup>> GetNCentralIntegrationsAsync(CancellationToken cancellationToken)
    {
        Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA N-central integrations", Status = "Reading integration records" });
        var rows = await GetNCentralIntegrationAccountsAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(MapNCentralIntegrationLookup).ToArray();
    }

    private async Task<IReadOnlyList<EntitySyncLookup>> GetNCentralIntegrationLinksLookupAsync(CancellationToken cancellationToken)
    {
        var links = await GetNCentralIntegrationLinksAsync(cancellationToken).ConfigureAwait(false);
        return links.Select(MapNCentralIntegrationLinkLookup).ToArray();
    }

    private async Task<IReadOnlyList<EntityIntegrationLink>> GetNCentralIntegrationLinksAsync(CancellationToken cancellationToken)
    {
        var integrationId = await ResolveNCentralIntegrationIdAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetNCentralIntegrationDetailsDocumentAsync(integrationId, cancellationToken).ConfigureAwait(false);
        return ParseNCentralIntegrationLinks(document.RootElement, integrationId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private async Task<JsonDocument> GetNCentralIntegrationDetailsDocumentAsync(int integrationId, CancellationToken cancellationToken)
    {
        var url = $"api/ncentraldetails/{integrationId}?includedetails=true&hasdisconnected=false";
        Progress?.Invoke(new EntitySyncProgress { Activity = "Get HaloPSA N-central links", Status = $"Reading integration {integrationId}" });
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, object?>> GetNCentralIntegrationDetailsAsync(int integrationId, CancellationToken cancellationToken)
    {
        using var document = await GetNCentralIntegrationDetailsDocumentAsync(integrationId, cancellationToken).ConfigureAwait(false);
        return JsonObjectToDictionary(document.RootElement);
    }

    private async Task<int> ResolveNCentralIntegrationIdAsync(CancellationToken cancellationToken)
    {
        if (options.NCentralIntegrationId > 0) return options.NCentralIntegrationId;

        var accounts = await GetNCentralIntegrationAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (accounts.Count == 1 && accounts[0].GetInt("id") is int id && id > 0) return id;
        var enabled = accounts.Where(account => account.GetBool("enableintegrator") == true).ToArray();
        if (enabled.Length == 1 && enabled[0].GetInt("id") is int enabledId && enabledId > 0) return enabledId;
        throw new InvalidOperationException("HaloPSA N-central integration id is required. Pass -HaloNCentralIntegrationId or set HALO_NCENTRAL_INTEGRATION_ID.");
    }

    private async Task<IReadOnlyList<JsonElement>> GetNCentralIntegrationAccountsAsync(CancellationToken cancellationToken)
    {
        const string url = "api/ncentraldetails?showall=true";
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("ncentraldetails", out var array) ? array : root;
        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return rows.EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private EntitySyncLookup MapNCentralIntegrationLookup(JsonElement row)
    {
        var lookup = new EntitySyncLookup
        {
            Vendor = Vendor,
            Type = EntitySyncLookupTypes.NCentralIntegration,
            Id = row.GetString("id") ?? string.Empty,
            Name = row.GetString("name") ?? string.Empty
        };
        AddProperty(lookup, "Url", row.GetString("url"));
        AddProperty(lookup, "TopLevel", row.GetString("toplevel"));
        AddProperty(lookup, "Username", row.GetString("username"));
        AddProperty(lookup, "EnableIntegrator", row.GetString("enableintegrator"));
        AddProperty(lookup, "LastSyncDate", row.GetString("lastsyncdate"));
        AddProperty(lookup, "LastSyncError", row.GetString("lastsyncerror"));
        AddProperty(lookup, "ServiceOrgId", ServiceOrgIdFromAlertUsername(row.GetString("alertusername")));
        return lookup;
    }

    private static EntitySyncLookup MapNCentralIntegrationLinkLookup(EntityIntegrationLink link)
    {
        var lookup = new EntitySyncLookup
        {
            Vendor = "HaloPSA",
            Type = EntitySyncLookupTypes.NCentralIntegrationLink,
            Id = link.LinkId,
            Name = link.SourceName
        };
        AddProperty(lookup, "IntegrationId", link.IntegrationId);
        AddProperty(lookup, "SourceVendor", link.SourceVendor);
        AddProperty(lookup, "SourceEntityType", link.SourceEntityType);
        AddProperty(lookup, "SourceId", link.SourceId);
        AddProperty(lookup, "TargetVendor", link.TargetVendor);
        AddProperty(lookup, "TargetEntityType", link.TargetEntityType);
        AddProperty(lookup, "TargetId", link.TargetId);
        AddProperty(lookup, "TargetName", link.TargetName);
        AddProperty(lookup, "ParentTargetId", link.ParentTargetId);
        AddProperty(lookup, "Primary", link.Primary.ToString());
        return lookup;
    }

    private static IEnumerable<EntityIntegrationLink> ParseNCentralIntegrationLinks(JsonElement root, string integrationId)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;

        if (root.TryGetPropertyIgnoreCase("client_links", out var clientLinks) && clientLinks.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in clientLinks.EnumerateArray())
            {
                var targetId = ValidThirdPartyId(row.GetString("third_party_id"));
                var sourceId = row.GetString("halo_id");
                if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) continue;
                yield return new EntityIntegrationLink
                {
                    SourceVendor = "HaloPSA",
                    SourceEntityType = "Client",
                    SourceId = sourceId,
                    SourceName = row.GetString("halo_desc") ?? string.Empty,
                    TargetVendor = "NCentral",
                    TargetEntityType = "Customer",
                    TargetId = targetId,
                    TargetName = row.GetString("third_party_desc") ?? string.Empty,
                    IntegrationId = row.GetString("details_id") ?? integrationId,
                    LinkId = row.GetString("id") ?? string.Empty,
                    ParentTargetId = ValidThirdPartyId(row.GetString("third_party_secondary_id")),
                    Primary = row.GetBool("primary") ?? false
                };
            }
        }

        if (root.TryGetPropertyIgnoreCase("site_links", out var siteLinks) && siteLinks.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in siteLinks.EnumerateArray())
            {
                var targetId = ValidThirdPartyId(row.GetString("third_party_id"));
                var sourceId = row.GetString("halo_id");
                if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) continue;
                yield return new EntityIntegrationLink
                {
                    SourceVendor = "HaloPSA",
                    SourceEntityType = "Site",
                    SourceId = sourceId,
                    SourceName = row.GetString("halo_desc") ?? string.Empty,
                    TargetVendor = "NCentral",
                    TargetEntityType = "Site",
                    TargetId = targetId,
                    TargetName = row.GetString("third_party_desc") ?? string.Empty,
                    IntegrationId = row.GetString("details_id") ?? integrationId,
                    LinkId = row.GetString("id") ?? string.Empty,
                    ParentTargetId = ValidThirdPartyId(row.GetString("third_party_secondary_id")),
                    Primary = row.GetBool("primary") ?? false
                };
            }
        }
    }

    private static string? ValidThirdPartyId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-1" ? null : value;
    }

    private static List<Dictionary<string, object?>> ReadMutableArray(Dictionary<string, object?> root, string propertyName)
    {
        if (!root.TryGetValue(propertyName, out var value) || value is not List<object?> array) return new List<Dictionary<string, object?>>();
        return array.OfType<Dictionary<string, object?>>().Select(item => new Dictionary<string, object?>(item, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static Dictionary<string, object?> JsonObjectToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Expected HaloPSA JSON object, got {element.ValueKind}.");
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) dictionary[property.Name] = JsonValueToObject(property.Value);
        return dictionary;
    }

    private static object? JsonValueToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonValueToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var intValue) ? intValue : element.TryGetInt64(out var longValue) ? longValue : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? StringValue(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            JsonElement json => json.GetString(),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static object IntOrString(string value)
    {
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue) ? intValue : value;
    }

    private static int IntOrDefault(object? value, int defaultValue)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            string text when int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue) => intValue,
            _ => defaultValue
        };
    }

    private static string? ServiceOrgIdFromAlertUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : null;
    }

    private async Task<JsonDocument> FetchClientListPageAsync(string url, CancellationToken cancellationToken)
    {
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string BuildClientListUrl(EntityQuery query, int pageSize, int pageNumber, string? includedCustomFieldIds)
    {
        var url = new StringBuilder("api/client?includeinactive=").Append(query.IncludeInactive ? "true" : "false");
        url.Append("&toplevel_id=").Append(options.TopLevelId);
        url.Append("&pageinate=true");
        url.Append("&page_size=").Append(pageSize);
        url.Append("&page_no=").Append(pageNumber);
        url.Append("&include_website=true");
        if (!string.IsNullOrWhiteSpace(includedCustomFieldIds)) url.Append("&include_custom_fields=").Append(Uri.EscapeDataString(includedCustomFieldIds));
        if (!string.IsNullOrWhiteSpace(query.Search)) url.Append("&search=").Append(Uri.EscapeDataString(query.Search));
        return url.ToString();
    }

    private async Task<string?> ResolveIncludedCustomFieldIdsAsync(string? requiredCustomFieldName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requiredCustomFieldName)) return null;
        var fieldNames = requiredCustomFieldName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fieldIds = new List<string>();
        foreach (var fieldName in fieldNames)
        {
            if (fieldName.Equals(options.NetSuiteCustomerIdField, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(options.NetSuiteCustomerIdFieldId))
                {
                    fieldIds.Add(options.NetSuiteCustomerIdFieldId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(netSuiteCustomerIdFieldIdCache)) netSuiteCustomerIdFieldIdCache = await ResolveCustomFieldIdAsync(fieldName, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(netSuiteCustomerIdFieldIdCache)) fieldIds.Add(netSuiteCustomerIdFieldIdCache);
                continue;
            }

            if (!fieldName.Equals(options.NetSuiteCustomerNameField, StringComparison.OrdinalIgnoreCase)) continue;
            var fieldId = await ResolveCustomFieldIdAsync(fieldName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fieldId)) fieldIds.Add(fieldId);
        }

        return fieldIds.Count == 0 ? null : string.Join(',', fieldIds.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveCustomFieldIdAsync(string fieldName, CancellationToken cancellationToken)
    {
        const string url = "api/FieldInfo?typeid=2";
        Trace?.Invoke("HaloPSA GET " + url);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Trace?.Invoke($"HaloPSA custom field metadata lookup failed with HTTP {(int)response.StatusCode}: {url}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var matched = FindCustomFieldId(document.RootElement, fieldName);
        if (!string.IsNullOrWhiteSpace(matched))
        {
            Trace?.Invoke($"HaloPSA custom field '{fieldName}' resolved to field ID {matched}.");
            return matched;
        }

        Trace?.Invoke($"HaloPSA custom field '{fieldName}' was not found in FieldInfo custom field setup.");
        return null;
    }

    private static string? FindCustomFieldId(JsonElement element, string fieldName)
    {
        foreach (var item in EnumerateObjects(element))
        {
            var name = item.GetString("name", "fieldname", "field_name", "fieldlabel", "field_label", "label", "displayname", "display_name", "inputname", "input_name", "caption");
            if (!FieldNameMatches(name, fieldName)) continue;
            var id = item.GetString("id", "fieldid", "field_id", "customfieldid", "custom_field_id", "customid", "custom_id");
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return null;
    }

    private static bool FieldNameMatches(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) || NormalizeFieldName(candidate) == NormalizeFieldName(expected);
    }

    private static string NormalizeFieldName(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        await AddConfiguredCustomFieldsAsync(request, cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new[] { await ToHaloClientPayloadAsync(request, false, true, cancellationToken).ConfigureAwait(false) });
        using var response = await PostJsonAsync("api/client", body, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Action = "Create", Success = response.IsSuccessStatusCode, Message = response.IsSuccessStatusCode ? null : text, Raw = text };
    }

    public async Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        await AddConfiguredCustomFieldsAsync(request, cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new[] { await ToHaloClientPayloadAsync(request, true, false, cancellationToken).ConfigureAwait(false) });
        using var response = await PostJsonAsync("api/client", body, cancellationToken).ConfigureAwait(false);
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

    public async Task<EntityWriteResult> UpsertNCentralClientLinkAsync(string haloClientId, string haloClientName, string nCentralCustomerId, string nCentralCustomerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(haloClientId)) throw new InvalidOperationException("HaloPSA N-central client link write requires a HaloPSA client ID.");
        if (string.IsNullOrWhiteSpace(nCentralCustomerId)) throw new InvalidOperationException("HaloPSA N-central client link write requires an N-central customer ID.");

        var integrationId = await ResolveNCentralIntegrationIdAsync(cancellationToken).ConfigureAwait(false);
        var root = await GetNCentralIntegrationDetailsAsync(integrationId, cancellationToken).ConfigureAwait(false);
        var clientLinks = ReadMutableArray(root, "client_links");
        var existingIndex = -1;
        for (var i = 0; i < clientLinks.Count; i++)
        {
            var linkHaloId = StringValue(clientLinks[i].TryGetValue("halo_id", out var existingHaloId) ? existingHaloId : null);
            var linkTargetId = ValidThirdPartyId(StringValue(clientLinks[i].TryGetValue("third_party_id", out var existingThirdPartyId) ? existingThirdPartyId : null));
            if (!string.IsNullOrWhiteSpace(linkTargetId) && linkTargetId.Equals(nCentralCustomerId, StringComparison.OrdinalIgnoreCase) && !haloClientId.Equals(linkHaloId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"N-central customer '{nCentralCustomerId}' is already linked to HaloPSA client '{linkHaloId}'. Refusing to create a duplicate HaloPSA N-central client link.");
            }

            if (haloClientId.Equals(linkHaloId, StringComparison.OrdinalIgnoreCase)) existingIndex = i;
        }

        var link = existingIndex >= 0 ? clientLinks[existingIndex] : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        link["table_id"] = 2;
        link["module_id"] = 242;
        link["halo_id"] = IntOrString(haloClientId);
        link["third_party_id"] = nCentralCustomerId;
        link["third_party_desc"] = nCentralCustomerName;
        link["third_party_type"] = StringValue(link.TryGetValue("third_party_type", out var thirdPartyType) ? thirdPartyType : null) ?? string.Empty;
        link["third_party_url"] = StringValue(link.TryGetValue("third_party_url", out var thirdPartyUrl) ? thirdPartyUrl : null) ?? string.Empty;
        link["third_party_assigned_to"] = StringValue(link.TryGetValue("third_party_assigned_to", out var thirdPartyAssignedTo) ? thirdPartyAssignedTo : null) ?? string.Empty;
        link["third_party_count"] = IntOrDefault(link.TryGetValue("third_party_count", out var thirdPartyCount) ? thirdPartyCount : null, 0);
        link["primary"] = true;
        link["halo_desc"] = haloClientName;
        link["halo_second_desc"] = StringValue(link.TryGetValue("halo_second_desc", out var haloSecondDesc) ? haloSecondDesc : null) ?? string.Empty;
        link["details_id"] = integrationId;
        link["third_party_secondary_id"] = StringValue(link.TryGetValue("third_party_secondary_id", out var thirdPartySecondaryId) ? thirdPartySecondaryId : null) ?? string.Empty;
        if (existingIndex >= 0) clientLinks[existingIndex] = link;
        else clientLinks.Add(link);

        var body = JsonSerializer.Serialize(new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["client_links"] = clientLinks, ["id"] = integrationId.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        using var response = await PostJsonAsync("api/ncentraldetails", body, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "NCentralIntegrationLink",
            Id = existingIndex >= 0 ? StringValue(link.TryGetValue("id", out var id) ? id : null) : null,
            Action = existingIndex >= 0 ? "UpdateClientLink" : "CreateClientLink",
            Success = response.IsSuccessStatusCode,
            Message = response.IsSuccessStatusCode ? null : text,
            Raw = text
        };
    }

    public async Task<EntityWriteResult> UpsertNCentralSiteLinkAsync(string haloSiteId, string haloSiteName, string haloClientName, string nCentralSiteId, string nCentralSiteName, string nCentralCustomerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(haloSiteId)) throw new InvalidOperationException("HaloPSA N-central site link write requires a HaloPSA site ID.");
        if (string.IsNullOrWhiteSpace(nCentralSiteId)) throw new InvalidOperationException("HaloPSA N-central site link write requires an N-central site ID.");
        if (string.IsNullOrWhiteSpace(nCentralCustomerId)) throw new InvalidOperationException("HaloPSA N-central site link write requires an N-central customer ID.");

        var integrationId = await ResolveNCentralIntegrationIdAsync(cancellationToken).ConfigureAwait(false);
        var root = await GetNCentralIntegrationDetailsAsync(integrationId, cancellationToken).ConfigureAwait(false);
        var siteLinks = ReadMutableArray(root, "site_links");
        var existingIndex = -1;
        for (var i = 0; i < siteLinks.Count; i++)
        {
            var linkHaloId = StringValue(siteLinks[i].TryGetValue("halo_id", out var existingHaloId) ? existingHaloId : null);
            var linkTargetId = ValidThirdPartyId(StringValue(siteLinks[i].TryGetValue("third_party_id", out var existingThirdPartyId) ? existingThirdPartyId : null));
            if (!string.IsNullOrWhiteSpace(linkTargetId) && linkTargetId.Equals(nCentralSiteId, StringComparison.OrdinalIgnoreCase) && !haloSiteId.Equals(linkHaloId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"N-central site '{nCentralSiteId}' is already linked to HaloPSA site '{linkHaloId}'. Refusing to create a duplicate HaloPSA N-central site link.");
            }

            if (haloSiteId.Equals(linkHaloId, StringComparison.OrdinalIgnoreCase)) existingIndex = i;
        }

        var link = existingIndex >= 0 ? siteLinks[existingIndex] : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        link["table_id"] = 3;
        link["module_id"] = 242;
        link["halo_id"] = IntOrString(haloSiteId);
        link["third_party_id"] = nCentralSiteId;
        link["third_party_desc"] = nCentralSiteName;
        link["third_party_type"] = StringValue(link.TryGetValue("third_party_type", out var thirdPartyType) ? thirdPartyType : null) ?? string.Empty;
        link["third_party_url"] = StringValue(link.TryGetValue("third_party_url", out var thirdPartyUrl) ? thirdPartyUrl : null) ?? string.Empty;
        link["third_party_assigned_to"] = StringValue(link.TryGetValue("third_party_assigned_to", out var thirdPartyAssignedTo) ? thirdPartyAssignedTo : null) ?? string.Empty;
        link["third_party_count"] = IntOrDefault(link.TryGetValue("third_party_count", out var thirdPartyCount) ? thirdPartyCount : null, 0);
        link["primary"] = true;
        link["halo_desc"] = haloSiteName;
        link["halo_second_desc"] = haloClientName;
        link["details_id"] = integrationId;
        link["third_party_secondary_id"] = nCentralCustomerId;
        if (existingIndex >= 0) siteLinks[existingIndex] = link;
        else siteLinks.Add(link);

        var body = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["client_links"] = ReadMutableArray(root, "client_links"),
                ["site_links"] = siteLinks,
                ["id"] = integrationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });
        using var response = await PostJsonAsync("api/ncentraldetails", body, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "NCentralIntegrationLink",
            Id = existingIndex >= 0 ? StringValue(link.TryGetValue("id", out var id) ? id : null) : null,
            Action = existingIndex >= 0 ? "UpdateSiteLink" : "CreateSiteLink",
            Success = response.IsSuccessStatusCode,
            Message = response.IsSuccessStatusCode ? null : text,
            Raw = text
        };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync("api/client?count=1", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose()
    {
        rateLimiter.Dispose();
        httpClient.Dispose();
    }

    private async Task<ExternalEntity> GetFullClientAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await GetAsync("api/client/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
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
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(entity.Id)) entity.ExternalIds["HaloPsaId"] = entity.Id;
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

                if (name.Equals(options.NetSuiteCustomerNameField, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    entity.CustomFields["NetSuiteCustomerName"] = value;
                }
            }
        }

        return entity;
    }

    private ExternalEntity MapSite(JsonElement item, ExternalEntity client)
    {
        var rawName = item.GetString("name", "site_name", "clientsite_name") ?? string.Empty;
        var name = rawName;
        var separator = name.IndexOf('/', StringComparison.Ordinal);
        if (separator >= 0 && separator + 1 < name.Length) name = name[(separator + 1)..];

        var clientId = item.GetString("client_id") ?? client.Id;
        var clientName = item.GetString("client_name") ?? client.Name;
        var entity = new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Site",
            Id = item.GetString("id", "site_id", "key") ?? string.Empty,
            Name = name,
            Email = item.GetString("accountsemailaddress", "accounts_email_address", "emailaddress", "email_address", "email", "mainemail", "main_email"),
            Phone = item.GetString("phonenumber", "phone_number", "telephone", "telephone_number", "phone", "mainphone", "main_phone", "tel"),
            Website = item.GetString("website", "web_site", "url") ?? client.Website,
            Domain = EntityNormalizer.NormalizeDomain(item.GetString("website", "web_site", "url") ?? client.Website, item.GetString("accountsemailaddress", "accounts_email_address", "emailaddress", "email_address", "email", "mainemail", "main_email")),
            PrimaryAddress = MapAddress(item),
            IsActive = item.GetBool("active", "isactive") ?? (item.GetBool("inactive", "isinactive") is bool inactive ? !inactive : null),
            CreatedAt = item.GetDate("datecreated", "created_at", "created"),
            UpdatedAt = item.GetDate("alastupdate", "last_update", "updated_at", "updated")
        };
        if (!string.IsNullOrWhiteSpace(entity.Id)) entity.ExternalIds["HaloPsaSiteId"] = entity.Id;
        if (!string.IsNullOrWhiteSpace(clientId)) entity.ExternalIds["HaloPsaClientId"] = clientId;
        entity.CustomFields["HaloPsaClientName"] = clientName;
        entity.CustomFields["HaloPsaClientSiteName"] = rawName;
        return entity;
    }

    private static EntityAddress MapAddress(JsonElement item)
    {
        if (TryGetAddressObject(item, out var address)) item = address;
        return new EntityAddress
        {
            Attention = ReadAddressString(item, "attention", "delivery_address_attention", "deliveryaddressattention", "invoice_address_attention", "invoiceaddressattention"),
            Line1 = ReadAddressString(item, "line1", "address1", "addr1", "address_1", "addressline1", "address_line1", "delivery_address_line1", "deliveryaddress1", "invoice_address_line1", "invoiceaddress1"),
            Line2 = ReadAddressString(item, "line2", "address2", "addr2", "address_2", "addressline2", "address_line2", "delivery_address_line2", "deliveryaddress2", "invoice_address_line2", "invoiceaddress2"),
            Line3 = ReadAddressString(item, "line5", "address5", "addr5", "address_5", "addressline5", "address_line5", "delivery_address_line5", "deliveryaddress5", "invoice_address_line5", "invoiceaddress5"),
            City = ReadAddressString(item, "city", "town", "line3", "address3", "addr3", "address_3", "addressline3", "address_line3", "delivery_address_line3", "deliveryaddress3", "invoice_address_line3", "invoiceaddress3"),
            State = ReadAddressString(item, "state", "county", "province", "region", "line4", "address4", "addr4", "address_4", "addressline4", "address_line4", "delivery_address_line4", "deliveryaddress4", "invoice_address_line4", "invoiceaddress4"),
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

    private async Task<Dictionary<string, object?>> ToHaloClientPayloadAsync(EntityWriteRequest request, bool includeId, bool isCreate, CancellationToken cancellationToken)
    {
        var relationship = await ResolveCustomerRelationshipAsync(request, cancellationToken).ConfigureAwait(false);
        var customerType = await ResolveCustomerTypeAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (includeId) payload["id"] = request.Id;
        payload["name"] = request.Name;
        payload["toplevel_id"] = options.TopLevelId;
        payload["colour"] = options.DefaultColour;
        payload["isclientdetails"] = true;
        if (relationship != null) payload["customer_relationship"] = new[] { new Dictionary<string, object?> { ["id"] = relationship.Id.ToString(), ["name"] = relationship.Name } };
        if (customerType != null) payload["customertype"] = customerType.Id.ToString();
        foreach (var field in request.Fields)
        {
            if (IsLookupMappingField(field.Key)) continue;
            if (includeId && IsPrimarySiteField(field.Key)) continue;
            if (isCreate && AddCreateSiteField(payload, field.Key, field.Value)) continue;
            payload[field.Key] = field.Value;
        }
        if (request.CustomFields.Count > 0)
        {
            payload["customfields"] = request.CustomFields.Select(field => new Dictionary<string, object?> { ["name"] = field.Key, ["value"] = field.Value }).ToArray();
        }
        return payload;
    }

    private async Task<LookupOption?> ResolveCustomerRelationshipAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var name = RequestString(request, "customer_relationship_name") ?? options.CustomerRelationshipName;
        var id = RequestString(request, "customer_relationship_id") ?? (options.CustomerRelationshipId > 0 ? options.CustomerRelationshipId.ToString() : null);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) return null;
        if (customerRelationshipCache != null && MatchesLookup(customerRelationshipCache, new[] { name, id })) return customerRelationshipCache;
        var relationships = await GetLookupAsync(CustomerRelationshipLookupId, null, cancellationToken).ConfigureAwait(false);
        var terms = new[] { name, id }.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term!);
        return customerRelationshipCache = FindLookup(relationships, terms) ?? throw new InvalidOperationException($"HaloPSA customer relationship '{name ?? id}' was not found in lookup {CustomerRelationshipLookupId}.");
    }

    private async Task<LookupOption?> ResolveCustomerTypeAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var name = RequestString(request, "customer_type_name") ?? options.CustomerTypeName;
        var id = RequestString(request, "customer_type_id") ?? (options.CustomerTypeId > 0 ? options.CustomerTypeId.ToString() : null);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) name = "Customer";
        if (customerTypeCache != null && MatchesLookup(customerTypeCache, new[] { name, id })) return customerTypeCache;
        var types = await GetLookupAsync(CustomerTypeLookupId, null, cancellationToken).ConfigureAwait(false);
        var terms = new[] { name, id }.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term!);
        var matched = FindLookup(types, terms);
        if (matched != null) return customerTypeCache = matched;
        var fallback = FindLookup(types, new[] { "Customer" });
        return customerTypeCache = fallback ?? throw new InvalidOperationException($"HaloPSA customer type '{name ?? id}' was not found in lookup {CustomerTypeLookupId}, and fallback type 'Customer' was not found.");
    }

    private static string? RequestString(EntityWriteRequest request, string fieldName)
    {
        return request.Fields.TryGetValue(fieldName, out var value) ? value?.ToString() : null;
    }

    private static bool MatchesLookup(LookupOption option, IEnumerable<string?> terms)
    {
        var normalizedTerms = terms.Where(term => !string.IsNullOrWhiteSpace(term)).Select(NormalizeLookupValue).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return option.Values.Select(NormalizeLookupValue).Any(normalizedTerms.Contains);
    }

    private async Task AddConfiguredCustomFieldsAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.AccountManagerEmail) || request.CustomFields.ContainsKey(options.AccountManagerField)) return;
        request.CustomFields[options.AccountManagerField] = await ResolveAccountManagerIdAsync(options.AccountManagerEmail, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveAccountManagerIdAsync(string email, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accountManagerIdCache)) return accountManagerIdCache;
        Trace?.Invoke("HaloPSA GET api/EmailAddressBook?iscachebuild=true");
        using var response = await GetAsync("api/EmailAddressBook?iscachebuild=true", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var row in EnumerateObjects(document.RootElement))
        {
            var candidateEmail = row.GetString("emailaddress", "email_address", "email", "address");
            if (!email.Equals(candidateEmail, StringComparison.OrdinalIgnoreCase)) continue;
            var id = row.GetString("agent_id", "agentid", "user_id", "userid", "id", "value");
            if (!string.IsNullOrWhiteSpace(id)) return accountManagerIdCache = id;
        }

        throw new InvalidOperationException($"HaloPSA account manager '{email}' was not found in EmailAddressBook.");
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjects(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item)) yield return nested;
            }
        }
    }

    private static bool AddCreateSiteField(Dictionary<string, object?> payload, string fieldName, object? value)
    {
        if (fieldName.Equals("clientsite_name", StringComparison.OrdinalIgnoreCase)) payload["newclient_sitename"] = value;
        else if (fieldName.Equals("phonenumber", StringComparison.OrdinalIgnoreCase)) payload["newclient_phonenumber"] = value;
        else if (fieldName.Equals("contactemail", StringComparison.OrdinalIgnoreCase)) payload["newclient_contactemail"] = value;
        else if (fieldName.Equals("delivery_address", StringComparison.OrdinalIgnoreCase)) payload["newclient_delivery_address"] = value;
        else if (fieldName.Equals("invoice_address", StringComparison.OrdinalIgnoreCase)) payload["newclient_invoice_address"] = value;
        else return false;
        return true;
    }

    private static bool IsPrimarySiteField(string fieldName)
    {
        return fieldName.Equals("clientsite_name", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("phonenumber", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("contactemail", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("delivery_address", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("invoice_address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLookupMappingField(string fieldName)
    {
        return fieldName.Equals("customer_relationship_name", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("customer_relationship_id", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("customer_type_name", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("customer_type_id", StringComparison.OrdinalIgnoreCase);
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
        using var response = await PostJsonAsync("api/Site", body, cancellationToken).ConfigureAwait(false);
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
        if (request.Fields.TryGetValue("contactemail", out var contactEmail) && contactEmail != null) payload["emailaddress"] = contactEmail;
        if (request.Fields.TryGetValue("clientsite_name", out var siteName) && siteName != null) payload["name"] = siteName;
        if (request.Fields.TryGetValue("invoice_address", out var invoiceAddressValue) && invoiceAddressValue is Dictionary<string, object?> invoiceAddress) payload["invoice_address"] = invoiceAddress;
        if (!request.Fields.TryGetValue("delivery_address", out var addressValue) || addressValue is not Dictionary<string, object?> address) return payload;

        payload["delivery_address"] = address;

        var siteAddress = new EntityAddress
        {
            Attention = address.TryGetValue("attention", out var attention) ? attention?.ToString() : null,
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
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLookupOptions(document.RootElement).ToArray();
    }

    private static IEnumerable<LookupOption> ParseLookupOptions(JsonElement root)
    {
        var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("lookup", out var lookup) ? lookup : root;
        if (rows.ValueKind == JsonValueKind.Object && rows.TryGetPropertyIgnoreCase("lookups", out var lookups)) rows = lookups;
        if (rows.ValueKind == JsonValueKind.Object && rows.TryGetPropertyIgnoreCase("toplevels", out var topLevels)) rows = topLevels;
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

    private static void AddProperty(EntitySyncLookup lookup, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lookup.Properties[name] = value;
    }

    private sealed record LookupOption(int Id, IReadOnlyList<string> Values)
    {
        public string Name => Values.FirstOrDefault() ?? Id.ToString();
    }

    private static readonly Dictionary<string, string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia", ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana", ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada", ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York", ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota", ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia", ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming", ["AB"] = "Alberta", ["BC"] = "British Columbia", ["MB"] = "Manitoba", ["NB"] = "New Brunswick", ["NL"] = "Newfoundland and Labrador", ["NS"] = "Nova Scotia", ["NT"] = "Northwest Territories", ["NU"] = "Nunavut", ["ON"] = "Ontario", ["PE"] = "Prince Edward Island", ["QC"] = "Quebec", ["SK"] = "Saskatchewan", ["YT"] = "Yukon"
    };

    private static readonly HashSet<string> CanadianCodes = new(StringComparer.OrdinalIgnoreCase) { "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT" };
    private static readonly Dictionary<string, string> StateCodes = StateNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnitedStatesCodes = StateNames.Keys.Where(code => code.Length == 2 && !CanadianCodes.Contains(code)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

}
