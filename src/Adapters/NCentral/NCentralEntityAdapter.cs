using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.NCentral;

public sealed class NCentralEntityAdapter : IEntityAdapter, IDisposable
{
    private const int DefaultPageSize = 1000;
    private const int MinimumRequestIntervalMs = 500;
    private const int MaxRateLimitRetries = 6;

    private readonly NCentralOptions options;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim requestThrottle = new(1, 1);
    private DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt = DateTimeOffset.MinValue;

    public NCentralEntityAdapter(NCentralOptions options)
    {
        this.options = options;
        httpClient = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl)) };
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => "NCentral";

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (query.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)) return await GetSitesAsync(query, cancellationToken).ConfigureAwait(false);
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("N-central adapter currently supports EntityType Customer and Site.");
        var customers = new List<ExternalEntity>();
        var pageNumber = 1;
        while (!query.Count.HasValue || customers.Count < query.Count.Value)
        {
            var pageSize = Math.Min(DefaultPageSize, query.Count.HasValue ? Math.Max(1, query.Count.Value - customers.Count) : DefaultPageSize);
            using var document = await GetJsonAsync(BuildCustomerListUrl(pageNumber, pageSize), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data) ? data : root;
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"N-central returned JSON, but not an array or an object with a 'data' array. Root type: {root.ValueKind}.");
            var pageCount = 0;
            foreach (var item in rows.EnumerateArray())
            {
                pageCount++;
                var entity = MapCustomer(item);
                if (!MatchesQuery(entity, query)) continue;
                customers.Add(entity);
                if (query.Count.HasValue && customers.Count >= query.Count.Value) break;
            }

            if (pageCount == 0 || pageCount < pageSize) break;
            if (root.ValueKind == JsonValueKind.Object && root.GetInt("totalPages") is int totalPages && pageNumber >= totalPages) break;
            pageNumber++;
        }

        return customers;
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)) return await CreateSiteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!request.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("N-central adapter currently supports creating EntityType Customer and Site.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("N-central customer creation requires a customer name.");
        if (string.IsNullOrWhiteSpace(options.ServiceOrgId)) throw new InvalidOperationException("N-central customer creation requires NCentralServiceOrgId. Reconnect with -NCentralServiceOrgId or set NCENTRAL_SERVICE_ORG_ID.");
        if (DesiredOrganizationProperties(request).Count > 0) EnsureSoapCredentials("N-central organization custom properties require SOAP credentials. Reconnect with -NCentralSoapUsername/-NCentralSoapPassword or set NCENTRAL_SOAP_USERNAME/NCENTRAL_SOAP_PASSWORD.");

        var payload = ToCustomerPayload(request);
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAuthenticatedAsync(HttpMethod.Post, $"api/service-orgs/{Uri.EscapeDataString(options.ServiceOrgId)}/customers", content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var createdCustomerId = response.IsSuccessStatusCode ? ReadCreatedCustomerId(text) : null;
        if (response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(createdCustomerId)) createdCustomerId = await ResolveCreatedCustomerIdAsync(payload, cancellationToken).ConfigureAwait(false);
        EntityWriteResult? customPropertyResult = null;
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(createdCustomerId)) customPropertyResult = await UpdateOrganizationPropertiesAsync(createdCustomerId, request, cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = request.EntityType,
            Id = createdCustomerId,
            Action = "Create",
            Success = response.IsSuccessStatusCode && (customPropertyResult == null || customPropertyResult.Success),
            Message = response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(createdCustomerId) ? "Customer created, but N-central did not return an ID and readback resolution did not find a unique customer." : customPropertyResult?.Message ?? (response.IsSuccessStatusCode ? null : text),
            Raw = customPropertyResult == null ? text : new { Customer = text, OrganizationProperties = customPropertyResult.Raw }
        };
    }

    public async Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.EntityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Id = request.Id, Action = "Update", Success = true, Message = "N-central REST OpenAPI does not expose a site update endpoint; no N-central site fields were changed." };
        }

        if (!request.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("N-central adapter currently supports updating EntityType Customer.");
        if (string.IsNullOrWhiteSpace(request.Id)) throw new InvalidOperationException("N-central customer update requires a target customer ID.");
        if (string.IsNullOrWhiteSpace(options.ServiceOrgId)) throw new InvalidOperationException("N-central customer update requires NCentralServiceOrgId. Reconnect with -NCentralServiceOrgId or set NCENTRAL_SERVICE_ORG_ID.");
        EnsureSoapCredentials("N-central customer update requires SOAP credentials. Reconnect with -NCentralSoapUsername/-NCentralSoapPassword or set NCENTRAL_SOAP_USERNAME/NCENTRAL_SOAP_PASSWORD.");
        var propertyIds = await ValidateDesiredOrganizationPropertiesAsync(request.Id, request, cancellationToken).ConfigureAwait(false);

        var settings = ToCustomerModifySettings(request);
        var response = await InvokeEi2IntMethodAsync("customerModify", settings, cancellationToken).ConfigureAwait(false);
        var customPropertyResult = await UpdateOrganizationPropertiesAsync(request.Id, request, cancellationToken, propertyIds).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = request.EntityType,
            Id = response > 0 ? response.ToString(System.Globalization.CultureInfo.InvariantCulture) : request.Id,
            Action = "Update",
            Success = response > 0 && customPropertyResult.Success,
            Message = response > 0 ? customPropertyResult.Message : "N-central SOAP customerModify returned no customer ID.",
            Raw = new { CustomerModify = response, OrganizationProperties = customPropertyResult.Raw }
        };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(HttpMethod.Get, "api/auth/validate", null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        if (type.Equals(EntitySyncLookupTypes.ServiceOrganization, StringComparison.OrdinalIgnoreCase)) return GetServiceOrganizationsAsync(cancellationToken);
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public async Task<EntityWriteResult> SetOrganizationCustomPropertyAsync(string customerId, string label, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId)) throw new InvalidOperationException("N-central organization custom property update requires a customer ID.");
        if (string.IsNullOrWhiteSpace(label)) throw new InvalidOperationException("N-central organization custom property update requires a property name.");
        return await UpdateOrganizationPropertiesAsync(customerId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [label.Trim()] = value ?? string.Empty }, "Customer", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EntitySyncLookup>> GetServiceOrganizationsAsync(CancellationToken cancellationToken)
    {
        var serviceOrganizations = new List<EntitySyncLookup>();
        var pageNumber = 1;
        while (true)
        {
            using var document = await GetJsonAsync($"api/service-orgs?pageNumber={pageNumber}&pageSize={DefaultPageSize}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data) ? data : root;
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"N-central returned JSON, but not an array or an object with a 'data' array. Root type: {root.ValueKind}.");

            var pageCount = 0;
            foreach (var item in rows.EnumerateArray())
            {
                pageCount++;
                serviceOrganizations.Add(MapServiceOrganization(item));
            }

            if (pageCount == 0 || pageCount < DefaultPageSize) break;
            if (root.ValueKind == JsonValueKind.Object && root.GetInt("totalItems") is int totalItems && serviceOrganizations.Count >= totalItems) break;
            pageNumber++;
        }

        return serviceOrganizations;
    }

    private async Task<IReadOnlyList<ExternalEntity>> GetSitesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        var sites = new List<ExternalEntity>();
        var pageNumber = 1;
        while (!query.Count.HasValue || sites.Count < query.Count.Value)
        {
            var pageSize = Math.Min(DefaultPageSize, query.Count.HasValue ? Math.Max(1, query.Count.Value - sites.Count) : DefaultPageSize);
            using var document = await GetJsonAsync($"api/sites?pageNumber={pageNumber}&pageSize={pageSize}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data) ? data : root;
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"N-central returned JSON, but not an array or an object with a 'data' array. Root type: {root.ValueKind}.");
            var pageCount = 0;
            foreach (var item in rows.EnumerateArray())
            {
                pageCount++;
                var entity = MapSite(item);
                if (!MatchesQuery(entity, query)) continue;
                sites.Add(entity);
                if (query.Count.HasValue && sites.Count >= query.Count.Value) break;
            }

            if (pageCount == 0 || pageCount < pageSize) break;
            if (root.ValueKind == JsonValueKind.Object && root.GetInt("totalItems") is int totalItems && pageNumber * pageSize >= totalItems) break;
            pageNumber++;
        }

        await AddParentCustomerNamesAsync(sites, cancellationToken).ConfigureAwait(false);
        return sites;
    }

    private async Task AddParentCustomerNamesAsync(IReadOnlyList<ExternalEntity> sites, CancellationToken cancellationToken)
    {
        var parentIds = sites
            .Select(site => site.GetExternalId("NCentralCustomerId"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (parentIds.Count == 0) return;

        var customerNames = await GetCustomerNamesByIdAsync(parentIds, cancellationToken).ConfigureAwait(false);
        foreach (var site in sites)
        {
            var parentId = site.GetExternalId("NCentralCustomerId");
            if (!string.IsNullOrWhiteSpace(parentId) && customerNames.TryGetValue(parentId, out var customerName)) site.CustomFields["NCentralCustomerName"] = customerName;
        }
    }

    private async Task<Dictionary<string, string>> GetCustomerNamesByIdAsync(IReadOnlySet<string> customerIds, CancellationToken cancellationToken)
    {
        var customers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pageNumber = 1;
        while (customers.Count < customerIds.Count)
        {
            using var document = await GetJsonAsync(BuildCustomerListUrl(pageNumber, DefaultPageSize), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data) ? data : root;
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"N-central returned JSON, but not an array or an object with a 'data' array. Root type: {root.ValueKind}.");
            var pageCount = 0;
            foreach (var item in rows.EnumerateArray())
            {
                pageCount++;
                var customer = MapCustomer(item);
                if (!string.IsNullOrWhiteSpace(customer.Id) && customerIds.Contains(customer.Id) && !string.IsNullOrWhiteSpace(customer.Name)) customers[customer.Id] = customer.Name;
            }

            if (pageCount == 0 || pageCount < DefaultPageSize) break;
            if (root.ValueKind == JsonValueKind.Object && root.GetInt("totalItems") is int totalItems && pageNumber * DefaultPageSize >= totalItems) break;
            pageNumber++;
        }

        return customers;
    }

    private async Task<EntityWriteResult> CreateSiteAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("N-central site creation requires a site name.");
        var customerId = NCentralCustomerId(request) ?? throw new InvalidOperationException("N-central site creation requires NCentralCustomerId from the parent customer link.");
        var payload = ToSitePayload(request);
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAuthenticatedAsync(HttpMethod.Post, $"api/customers/{Uri.EscapeDataString(customerId)}/sites", content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = request.EntityType,
            Id = response.IsSuccessStatusCode ? ReadCreatedSiteId(text) : null,
            Action = "Create",
            Success = response.IsSuccessStatusCode,
            Message = response.IsSuccessStatusCode ? null : text,
            Raw = text
        };
    }

    public void Dispose()
    {
        requestThrottle.Dispose();
        httpClient.Dispose();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"N-central request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. URI: {url}. Response preview: {Preview(text)}");
        return JsonDocument.Parse(text);
    }

    private string BuildCustomerListUrl(int pageNumber, int pageSize)
    {
        var path = string.IsNullOrWhiteSpace(options.ServiceOrgId)
            ? "api/customers"
            : $"api/service-orgs/{Uri.EscapeDataString(options.ServiceOrgId)}/customers";
        return $"{path}?pageNumber={pageNumber}&pageSize={pageSize}";
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendWithRateLimitAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, url) { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                Trace?.Invoke("N-central " + method.Method + " " + url);
                return request;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken) && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        using var response = await SendWithRateLimitAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/authenticate");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.UserApiToken);
                Trace?.Invoke("N-central POST api/auth/authenticate");
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"N-central authentication failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response preview: {Preview(text)}");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetPropertyIgnoreCase("tokens", out var tokens) || !tokens.TryGetPropertyIgnoreCase("access", out var access)) throw new InvalidOperationException("N-central authentication response did not include tokens.access.");
        accessToken = access.GetString("token") ?? throw new InvalidOperationException("N-central authentication response did not include an access token.");
        var expirySeconds = access.GetInt("expirySeconds") ?? 3600;
        accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expirySeconds));
    }

    private async Task<HttpResponseMessage> SendWithRateLimitAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using var request = createRequest();
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitRetries) return response;

            var delay = RateLimitHelper.RateLimitDelay(response, attempt);
            Trace?.Invoke($"N-central rate limit reached. Waiting {(int)delay.TotalSeconds}s before retry {attempt + 1}/{MaxRateLimitRetries}.");
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await requestThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (nextRequestAt > now) await Task.Delay(nextRequestAt - now, cancellationToken).ConfigureAwait(false);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(MinimumRequestIntervalMs);
        }
        finally
        {
            requestThrottle.Release();
        }
    }

    private static ExternalEntity MapCustomer(JsonElement item)
    {
        var id = item.GetString("customerId", "orgUnitId") ?? string.Empty;
        var email = item.GetString("contactEmail");
        var phone = item.GetString("contactPhone", "phone");
        var entity = new ExternalEntity
        {
            Vendor = "NCentral",
            EntityType = "Customer",
            Id = id,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NCentralCustomerId"] = id },
            Name = item.GetString("customerName", "orgUnitName") ?? string.Empty,
            Email = email,
            Phone = phone,
            Domain = EntityNormalizer.NormalizeDomain(null, email),
            IsActive = true,
            PrimaryAddress = new EntityAddress
            {
                Line1 = item.GetString("street1"),
                Line2 = item.GetString("street2"),
                City = item.GetString("city"),
                State = item.GetString("stateProv"),
                PostalCode = item.GetString("postalCode"),
                Country = item.GetString("country")
            }
        };
        AddExternalId(entity, "NCentralExternalId", item.GetString("externalId"));
        AddExternalId(entity, "NCentralExternalId2", item.GetString("externalId2"));
        AddExternalId(entity, "NCentralParentId", item.GetString("parentId"));
        AddCustomField(entity, "NCentralOrgUnitType", item.GetString("orgUnitType"));
        AddCustomField(entity, "NCentralContactFirstName", item.GetString("contactFirstName"));
        AddCustomField(entity, "NCentralContactLastName", item.GetString("contactLastName"));
        AddCustomField(entity, "NCentralIsSystem", item.GetString("isSystem"));
        AddCustomField(entity, "NCentralIsServiceOrg", item.GetString("isServiceOrg"));
        return entity;
    }

    private static ExternalEntity MapSite(JsonElement item)
    {
        var id = item.GetString("siteId", "orgUnitId") ?? string.Empty;
        var parentId = item.GetString("parentId", "customerId");
        var email = item.GetString("contactEmail");
        var phone = item.GetString("contactPhone", "phone");
        var entity = new ExternalEntity
        {
            Vendor = "NCentral",
            EntityType = "Site",
            Id = id,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NCentralSiteId"] = id },
            Name = item.GetString("siteName", "orgUnitName") ?? string.Empty,
            Email = email,
            Phone = phone,
            Domain = EntityNormalizer.NormalizeDomain(null, email),
            IsActive = true,
            PrimaryAddress = new EntityAddress
            {
                Line1 = item.GetString("street1"),
                Line2 = item.GetString("street2"),
                City = item.GetString("city"),
                State = item.GetString("stateProv"),
                PostalCode = item.GetString("postalCode"),
                Country = item.GetString("country")
            }
        };
        AddExternalId(entity, "NCentralCustomerId", parentId);
        AddExternalId(entity, "NCentralExternalId", item.GetString("externalId"));
        AddExternalId(entity, "NCentralExternalId2", item.GetString("externalId2"));
        AddCustomField(entity, "NCentralOrgUnitType", item.GetString("orgUnitType"));
        AddCustomField(entity, "NCentralContactFirstName", item.GetString("contactFirstName"));
        AddCustomField(entity, "NCentralContactLastName", item.GetString("contactLastName"));
        return entity;
    }

    private static EntitySyncLookup MapServiceOrganization(JsonElement item)
    {
        var lookup = new EntitySyncLookup
        {
            Vendor = "NCentral",
            Type = EntitySyncLookupTypes.ServiceOrganization,
            Id = item.GetString("soId") ?? string.Empty,
            Name = item.GetString("soName") ?? string.Empty
        };
        AddProperty(lookup, "OrgUnitType", item.GetString("orgUnitType"));
        AddProperty(lookup, "ParentId", item.GetString("parentId"));
        AddProperty(lookup, "ExternalId", item.GetString("externalId"));
        AddProperty(lookup, "ExternalId2", item.GetString("externalId2"));
        AddProperty(lookup, "ContactFirstName", item.GetString("contactFirstName"));
        AddProperty(lookup, "ContactLastName", item.GetString("contactLastName"));
        AddProperty(lookup, "ContactEmail", item.GetString("contactEmail"));
        AddProperty(lookup, "Phone", item.GetString("phone", "contactPhone"));
        return lookup;
    }

    private static bool MatchesQuery(ExternalEntity entity, EntityQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Search)) return true;
        var search = query.Search.Trim();
        return Contains(entity.Name, search)
            || Contains(entity.Email, search)
            || entity.ExternalIds.Values.Any(value => Contains(value, search));
    }

    private static bool Contains(string? value, string search) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private Dictionary<string, object?> ToCustomerPayload(EntityWriteRequest request)
    {
        var customerName = SanitizeNCentralName(request.Name);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["customerName"] = customerName
        };

        AddField(payload, "externalId", NCentralExternalId(request));
        AddField(payload, "contactFirstName", GetFieldString(request, "contactFirstName", "contactfirstname", "firstName", "firstname"));
        AddField(payload, "contactLastName", GetFieldString(request, "contactLastName", "contactlastname", "lastName", "lastname"));
        AddField(payload, "contactEmail", GetFieldString(request, "contactEmail", "contactemail", "email"));
        AddField(payload, "phone", GetFieldString(request, "phone", "phonenumber"));
        AddField(payload, "contactPhone", GetFieldString(request, "contactPhone", "contactphone", "phonenumber", "phone"));

        var address = GetAddress(request);
        if (address != null)
        {
            AddField(payload, "street1", GetAddressField(address, "street1", "line1", "address1"));
            AddField(payload, "street2", GetAddressField(address, "street2", "line2", "address2"));
            AddField(payload, "city", GetAddressField(address, "city", "line3"));
            AddField(payload, "stateProv", GetAddressField(address, "stateProv", "state", "province", "line4"));
            AddField(payload, "postalCode", GetAddressField(address, "postalCode", "postalcode", "postcode", "zip"));
            AddField(payload, "country", NormalizeNCentralCountryCode(GetAddressField(address, "country")));
        }

        payload.TryAdd("licenseType", "Professional");

        return payload;
    }

    private IReadOnlyList<KeyValuePair<string, string>> ToCustomerModifySettings(EntityWriteRequest request)
    {
        var payload = ToCustomerPayload(request);
        var settings = new List<KeyValuePair<string, string>>
        {
            new("customerid", request.Id ?? string.Empty),
            new("parentid", options.ServiceOrgId),
            new("customername", ToStringValue(payload["customerName"]) ?? string.Empty)
        };

        AddSetting(settings, "externalid", ToStringValue(payload.TryGetValue("externalId", out var externalId) ? externalId : null));
        AddSetting(settings, "telephone", ToStringValue(payload.TryGetValue("phone", out var phone) ? phone : null) ?? ToStringValue(payload.TryGetValue("contactPhone", out var contactPhoneValue) ? contactPhoneValue : null));
        AddSetting(settings, "firstname", ToStringValue(payload.TryGetValue("contactFirstName", out var firstName) ? firstName : null));
        AddSetting(settings, "lastname", ToStringValue(payload.TryGetValue("contactLastName", out var lastName) ? lastName : null));
        AddSetting(settings, "email", ToStringValue(payload.TryGetValue("contactEmail", out var email) ? email : null));
        AddSetting(settings, "contact_telephone", ToStringValue(payload.TryGetValue("contactPhone", out var contactPhone) ? contactPhone : null));
        AddSetting(settings, "street1", ToStringValue(payload.TryGetValue("street1", out var street1) ? street1 : null));
        AddSetting(settings, "street2", ToStringValue(payload.TryGetValue("street2", out var street2) ? street2 : null));
        AddSetting(settings, "city", ToStringValue(payload.TryGetValue("city", out var city) ? city : null));
        AddSetting(settings, "state/province", ToStringValue(payload.TryGetValue("stateProv", out var stateProv) ? stateProv : null));
        AddSetting(settings, "zip/postalcode", ToStringValue(payload.TryGetValue("postalCode", out var postalCode) ? postalCode : null));
        AddSetting(settings, "country", ToStringValue(payload.TryGetValue("country", out var country) ? country : null));
        AddSetting(settings, "licensetype", ToStringValue(payload.TryGetValue("licenseType", out var licenseType) ? licenseType : null));
        return settings;
    }

    private Dictionary<string, object?> ToSitePayload(EntityWriteRequest request)
    {
        var siteName = SanitizeNCentralName(request.Name);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["siteName"] = siteName
        };

        AddField(payload, "externalId", NCentralExternalId(request));
        AddField(payload, "contactFirstName", GetFieldString(request, "contactFirstName", "contactfirstname", "firstName", "firstname"));
        AddField(payload, "contactLastName", GetFieldString(request, "contactLastName", "contactlastname", "lastName", "lastname"));
        AddField(payload, "contactEmail", GetFieldString(request, "contactEmail", "contactemail", "email"));
        AddField(payload, "phone", GetFieldString(request, "phone", "phonenumber"));
        AddField(payload, "contactPhone", GetFieldString(request, "contactPhone", "contactphone", "phonenumber", "phone"));

        var address = GetAddress(request);
        if (address != null)
        {
            AddField(payload, "street1", GetAddressField(address, "street1", "line1", "address1"));
            AddField(payload, "street2", GetAddressField(address, "street2", "line2", "address2"));
            AddField(payload, "city", GetAddressField(address, "city", "line3"));
            AddField(payload, "stateProv", GetAddressField(address, "stateProv", "state", "province", "line4"));
            AddField(payload, "postalCode", GetAddressField(address, "postalCode", "postalcode", "postcode", "zip"));
            AddField(payload, "country", NormalizeNCentralCountryCode(GetAddressField(address, "country")));
        }

        payload.TryAdd("licenseType", "Professional");
        return payload;
    }

    private async Task<int> InvokeEi2IntMethodAsync(string methodName, IReadOnlyList<KeyValuePair<string, string>> settings, CancellationToken cancellationToken)
    {
        var document = await InvokeEi2MethodAsync(BuildEi2SettingsMethod(methodName, settings), methodName, cancellationToken).ConfigureAwait(false);
        return ReadSoapIntResult(document, methodName);
    }

    private XElement BuildEi2SettingsMethod(string methodName, IReadOnlyList<KeyValuePair<string, string>> settings)
    {
        XNamespace ns = options.SoapNamespace;
        var method = new XElement(ns + methodName,
            new XElement(ns + "username", options.SoapUsername),
            new XElement(ns + "password", options.SoapPassword));
        foreach (var setting in settings)
        {
            method.Add(new XElement(ns + "settings",
                new XElement(ns + "key", setting.Key),
                new XElement(ns + "value", setting.Value)));
        }

        return method;
    }

    private async Task<XDocument> InvokeEi2MethodAsync(XElement method, string methodName, CancellationToken cancellationToken)
    {
        var endpoint = options.SoapEndpointPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(options.SoapEndpointPath, UriKind.Absolute)
            : new Uri(httpClient.BaseAddress!, options.SoapEndpointPath.TrimStart('/'));
        var body = BuildEi2SoapEnvelope(method);
        using var response = await SendWithRateLimitAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/soap+xml")
                };
                Trace?.Invoke("N-central SOAP " + methodName + " " + endpoint.PathAndQuery);
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"N-central SOAP {methodName} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response preview: {Preview(text)}");
        var document = XDocument.Parse(text);
        var fault = document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("Fault", StringComparison.OrdinalIgnoreCase));
        if (fault != null) throw new InvalidOperationException($"N-central SOAP {methodName} returned a fault: {Preview(fault.Value)}");
        return document;
    }

    private static string BuildEi2SoapEnvelope(XElement method)
    {
        XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";

        var document = new XDocument(new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ei2", method.Name.NamespaceName),
            new XElement(soap + "Body", method)));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static int ReadSoapIntResult(XDocument document, string methodName)
    {
        var result = document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals(methodName + "Return", StringComparison.OrdinalIgnoreCase) || element.Name.LocalName.Equals("return", StringComparison.OrdinalIgnoreCase));
        if (result == null || !int.TryParse(result.Value, out var value)) throw new InvalidOperationException($"N-central SOAP {methodName} response did not include an integer return value. Response preview: {Preview(document.ToString(SaveOptions.DisableFormatting))}");
        return value;
    }

    private async Task<EntityWriteResult> UpdateOrganizationPropertiesAsync(string customerId, EntityWriteRequest request, CancellationToken cancellationToken, IReadOnlyDictionary<string, int>? propertyIds = null)
    {
        var desired = DesiredOrganizationProperties(request);
        return await UpdateOrganizationPropertiesAsync(customerId, desired, request.EntityType, cancellationToken, propertyIds).ConfigureAwait(false);
    }

    private async Task<EntityWriteResult> UpdateOrganizationPropertiesAsync(string customerId, IReadOnlyDictionary<string, string> desired, string entityType, CancellationToken cancellationToken, IReadOnlyDictionary<string, int>? propertyIds = null)
    {
        if (desired.Count == 0) return new EntityWriteResult { Vendor = Vendor, EntityType = entityType, Id = customerId, Action = "UpdateOrganizationProperties", Success = true, Message = "No N-central organization custom properties to update." };
        EnsureSoapCredentials("N-central organization custom properties require SOAP credentials. Reconnect with -NCentralSoapUsername/-NCentralSoapPassword or set NCENTRAL_SOAP_USERNAME/NCENTRAL_SOAP_PASSWORD.");
        if (!int.TryParse(customerId, out var numericCustomerId)) throw new InvalidOperationException($"N-central organization custom properties require a numeric customer ID. Value: '{customerId}'.");

        propertyIds ??= await GetOrganizationPropertyIdsByLabelAsync(numericCustomerId, cancellationToken).ConfigureAwait(false);
        var missing = desired.Keys.Where(label => !propertyIds.ContainsKey(label)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("N-central organization custom properties were not found by label: " + string.Join(", ", missing) + ". Create them in N-central or reconnect with the matching -NCentral*PropertyLabel values.");

        XNamespace ns = options.SoapNamespace;
        var properties = desired.Select(property => new XElement(ns + "properties",
            new XElement(ns + "propertyId", propertyIds[property.Key]),
            new XElement(ns + "value", property.Value)));
        var method = new XElement(ns + "organizationPropertyModify",
            new XElement(ns + "username", options.SoapUsername),
            new XElement(ns + "password", options.SoapPassword),
            new XElement(ns + "organizationProperties",
                new XElement(ns + "customerId", numericCustomerId),
                properties));
        var response = await InvokeEi2MethodAsync(method, "organizationPropertyModify", cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = entityType, Id = customerId, Action = "UpdateOrganizationProperties", Success = true, Raw = response.ToString(SaveOptions.DisableFormatting) };
    }

    private async Task<IReadOnlyDictionary<string, int>?> ValidateDesiredOrganizationPropertiesAsync(string customerId, EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var desired = DesiredOrganizationProperties(request);
        if (desired.Count == 0) return null;
        if (!int.TryParse(customerId, out var numericCustomerId)) throw new InvalidOperationException($"N-central organization custom properties require a numeric customer ID. Value: '{customerId}'.");
        var propertyIds = await GetOrganizationPropertyIdsByLabelAsync(numericCustomerId, cancellationToken).ConfigureAwait(false);
        var missing = desired.Keys.Where(label => !propertyIds.ContainsKey(label)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("N-central organization custom properties were not found by label: " + string.Join(", ", missing) + ". Create them in N-central or reconnect with the matching -NCentral*PropertyLabel values.");
        return propertyIds;
    }

    private async Task<IReadOnlyDictionary<string, int>> GetOrganizationPropertyIdsByLabelAsync(int customerId, CancellationToken cancellationToken)
    {
        XNamespace ns = options.SoapNamespace;
        var method = new XElement(ns + "organizationPropertyList",
            new XElement(ns + "username", options.SoapUsername),
            new XElement(ns + "password", options.SoapPassword),
            new XElement(ns + "customerIds", customerId),
            new XElement(ns + "reverseOrder", "false"));
        var response = await InvokeEi2MethodAsync(method, "organizationPropertyList", cancellationToken).ConfigureAwait(false);
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in response.Descendants().Where(element => element.Name.LocalName.Equals("properties", StringComparison.OrdinalIgnoreCase)))
        {
            var label = property.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("label", StringComparison.OrdinalIgnoreCase))?.Value;
            var propertyIdText = property.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("propertyId", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(label) && int.TryParse(propertyIdText, out var propertyId)) labels[label] = propertyId;
        }

        return labels;
    }

    private Dictionary<string, string> DesiredOrganizationProperties(EntityWriteRequest request)
    {
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDesiredProperty(desired, options.HaloPsaIdPropertyLabel, CustomField(request, "HaloPsaId", "HaloPSAId", "externalId"));
        AddDesiredProperty(desired, options.NetSuiteIdPropertyLabel, CustomField(request, "NetSuiteId", "NetSuiteInternalId", "CFNetSuiteCustomerID"));
        AddDesiredProperty(desired, options.NetSuiteNamePropertyLabel, CustomField(request, "NetSuiteCustomerName", "NetSuiteName", "CFNetSuiteCustomerName"));
        return desired;
    }

    private static string? CustomField(EntityWriteRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            if (request.CustomFields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static void AddDesiredProperty(Dictionary<string, string> desired, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value)) desired[label] = value;
    }

    private void EnsureSoapCredentials(string message)
    {
        if (string.IsNullOrWhiteSpace(options.SoapUsername) || string.IsNullOrWhiteSpace(options.SoapPassword)) throw new InvalidOperationException(message);
    }

    private static void AddSetting(List<KeyValuePair<string, string>> settings, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) settings.Add(new KeyValuePair<string, string>(key, value.Trim()));
    }

    private async Task<string?> ResolveCreatedCustomerIdAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var customerName = ToStringValue(payload.TryGetValue("customerName", out var nameValue) ? nameValue : null);
        var externalId = ToStringValue(payload.TryGetValue("externalId", out var externalIdValue) ? externalIdValue : null);
        if (string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(externalId)) return null;

        var matches = new List<ExternalEntity>();
        var pageNumber = 1;
        while (true)
        {
            using var document = await GetJsonAsync(BuildCustomerListUrl(pageNumber, DefaultPageSize), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data) ? data : root;
            if (rows.ValueKind != JsonValueKind.Array) return null;

            var pageCount = 0;
            foreach (var item in rows.EnumerateArray())
            {
                pageCount++;
                var customer = MapCustomer(item);
                if (CustomerMatchesCreatePayload(customer, customerName, externalId)) matches.Add(customer);
            }

            if (pageCount == 0 || pageCount < DefaultPageSize) break;
            if (root.ValueKind == JsonValueKind.Object && root.GetInt("totalItems") is int totalItems && pageNumber * DefaultPageSize >= totalItems) break;
            pageNumber++;
        }

        var ids = matches.Select(match => match.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    private static bool CustomerMatchesCreatePayload(ExternalEntity customer, string? customerName, string? externalId)
    {
        if (!string.IsNullOrWhiteSpace(externalId) && customer.ExternalIds.TryGetValue("NCentralExternalId", out var existingExternalId) && externalId.Equals(existingExternalId, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(customerName) && customer.Name.Equals(customerName, StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeNCentralName(string value)
    {
        var sanitized = value.Replace("&", " and ", StringComparison.Ordinal);
        return string.Join(" ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public static string? NormalizeNCentralCountryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 2 && trimmed.All(char.IsLetter)) return trimmed.ToUpperInvariant();
        var normalized = trimmed.Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Equals("US", StringComparison.OrdinalIgnoreCase) || normalized.Equals("USA", StringComparison.OrdinalIgnoreCase) || normalized.Equals("UnitedStates", StringComparison.OrdinalIgnoreCase) || normalized.Equals("UnitedStatesOfAmerica", StringComparison.OrdinalIgnoreCase)) return "US";
        return trimmed;
    }

    private static Dictionary<string, object?>? GetAddress(EntityWriteRequest request)
    {
        foreach (var name in new[] { "delivery_address", "invoice_address", "address" })
        {
            if (!request.Fields.TryGetValue(name, out var value) || value == null) continue;
            if (value is Dictionary<string, object?> dictionary) return dictionary;
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary) return new Dictionary<string, object?>(readOnlyDictionary, StringComparer.OrdinalIgnoreCase);
            if (value is JsonElement { ValueKind: JsonValueKind.Object } json) return JsonToDictionary(json);
        }

        return null;
    }

    private static Dictionary<string, object?> JsonToDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return dictionary;
    }

    private static string? GetFieldString(EntityWriteRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            if (request.Fields.TryGetValue(name, out var value))
            {
                var text = ToStringValue(value);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
    }

    private static string? GetAddressField(Dictionary<string, object?> address, params string[] names)
    {
        foreach (var name in names)
        {
            if (address.TryGetValue(name, out var value))
            {
                var text = ToStringValue(value);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
    }

    private static string? ToStringValue(object? value)
    {
        if (value == null) return null;
        if (value is string text) return text;
        if (value is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number => json.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FirstNonEmpty(IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? NCentralExternalId(EntityWriteRequest request)
    {
        foreach (var name in new[] { "externalId", "NCentralExternalId" })
        {
            if (request.CustomFields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return FirstNonEmpty(request.CustomFields.Values);
    }

    private static string? NCentralCustomerId(EntityWriteRequest request)
    {
        foreach (var name in new[] { "NCentralCustomerId", "customerId", "parentId" })
        {
            if (request.CustomFields.TryGetValue(name, out var customValue) && !string.IsNullOrWhiteSpace(customValue)) return customValue;
            if (request.Fields.TryGetValue(name, out var fieldValue) && !string.IsNullOrWhiteSpace(ToStringValue(fieldValue))) return ToStringValue(fieldValue);
        }

        return null;
    }

    private static void AddField(Dictionary<string, object?> payload, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) payload[name] = value.Trim();
    }

    private static string? ReadCreatedCustomerId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0) return data[0].GetString("customerId", "orgUnitId", "id");
                if (data.ValueKind == JsonValueKind.Object) return data.GetString("customerId", "orgUnitId", "id");
            }

            return root.GetString("customerId", "orgUnitId", "id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadCreatedSiteId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetPropertyIgnoreCase("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0) return data[0].GetString("siteId", "orgUnitId", "id");
                if (data.ValueKind == JsonValueKind.Object) return data.GetString("siteId", "orgUnitId", "id");
            }

            return root.GetString("siteId", "orgUnitId", "id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddExternalId(ExternalEntity entity, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) entity.ExternalIds[name] = value;
    }

    private static void AddCustomField(ExternalEntity entity, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) entity.CustomFields[name] = value;
    }

    private static void AddProperty(EntitySyncLookup lookup, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lookup.Properties[name] = value;
    }

    private static string Preview(string text)
    {
        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "...";
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}
