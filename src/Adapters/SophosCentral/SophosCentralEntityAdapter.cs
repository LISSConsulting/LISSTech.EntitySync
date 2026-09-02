using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.SophosCentral;

public sealed class SophosCentralEntityAdapter : IEntityAdapter, IDisposable
{
    public const string TenantExternalIdName = "SophosCentralTenantId";
    public const string HaloTenantCustomFieldName = "CFSophosCentralTenantID";
    private const int MaximumPagesPerQuery = 1000;
    private const int MaximumPageSize = 100;

    private readonly SophosCentralOptions options;
    private readonly HttpClient httpClient;
    private readonly RateLimitedHttpRequester rateLimiter = new(EntitySyncVendors.SophosCentral);
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private readonly Uri identityUri;
    private readonly Uri globalApiUri;
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;
    private SophosIdentity? identity;

    public SophosCentralEntityAdapter(SophosCentralOptions options)
        : this(options, CreateProductionClient(options))
    {
    }

    internal SophosCentralEntityAdapter(SophosCentralOptions options, HttpMessageHandler handler)
        : this(options, CreateTestClient(options, handler))
    {
    }

    private SophosCentralEntityAdapter(SophosCentralOptions options, HttpClient httpClient)
    {
        this.options = options;
        this.httpClient = httpClient;
        identityUri = new Uri(options.IdentityUrl, UriKind.Absolute);
        globalApiUri = new Uri(UrlHelpers.EnsureTrailingSlash(options.GlobalApiUrl), UriKind.Absolute);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => EntitySyncVendors.SophosCentral;

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        EnsureCustomerEntityType(query.EntityType);
        var sophosIdentity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        var entities = new List<ExternalEntity>();
        var requestedTotal = query.Count;
        var pageSize = Math.Min(requestedTotal.GetValueOrDefault(MaximumPageSize), MaximumPageSize);
        pageSize = Math.Max(1, pageSize);
        var page = 1;
        var totalPages = 1;

        do
        {
            if (page > MaximumPagesPerQuery)
                throw new InvalidOperationException($"Sophos Central query exceeded the {MaximumPagesPerQuery}-page scan limit.");

            using var document = await GetTenantsPageAsync(sophosIdentity, page, pageSize, page == 1, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetPropertyIgnoreCase("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Sophos Central tenants response did not include an items array.");
            if (!root.TryGetPropertyIgnoreCase("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Sophos Central tenants response did not include page metadata.");

            var currentPage = pages.GetInt("current")
                ?? throw new InvalidOperationException("Sophos Central tenants response did not include the current page number.");
            if (currentPage != page)
                throw new InvalidOperationException($"Sophos Central tenants response returned page {currentPage} while page {page} was requested.");

            if (page == 1)
            {
                totalPages = pages.GetInt("total")
                    ?? throw new InvalidOperationException("Sophos Central tenants response did not include the total page count.");
                if (totalPages < 1 || totalPages > MaximumPagesPerQuery)
                    throw new InvalidOperationException($"Sophos Central tenants response reported an invalid total page count of {totalPages}.");
            }

            foreach (var item in items.EnumerateArray())
            {
                var entity = MapTenant(item);
                if (!query.IncludeInactive && entity.IsActive == false) continue;
                if (!MatchesQuery(entity, query)) continue;
                entities.Add(entity);
                if (requestedTotal.HasValue && entities.Count >= requestedTotal.Value) return entities;
            }

            page++;
        }
        while (page <= totalPages);

        return entities;
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        EnsureCustomerEntityType(request.EntityType);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException("Sophos Central tenant name is required.");

        var sophosIdentity = await GetWritablePartnerIdentityAsync(cancellationToken).ConfigureAwait(false);
        var payload = CreateTenantPayload(request);
        var uri = new Uri(globalApiUri, sophosIdentity.TenantsPath);
        Trace?.Invoke($"Sophos Central POST {sophosIdentity.TenantsPath}");
        using var response = await rateLimiter.SendAsync(
            httpClient,
            () => CreateTenantWriteRequest(HttpMethod.Post, uri, sophosIdentity, payload, "application/json"),
            Trace,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonResponseAsync(response, "tenant creation", cancellationToken).ConfigureAwait(false);
        var created = MapTenant(document.RootElement);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "Customer",
            Id = created.Id,
            Action = "Create",
            Success = true,
            Message = $"Created Sophos Central tenant '{created.Name}'.",
            Raw = document.RootElement.Clone()
        };
    }

    public async Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        EnsureCustomerEntityType(request.EntityType);
        if (string.IsNullOrWhiteSpace(request.Id))
            return await CreateEntityAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException("Sophos Central tenant display name is required.");

        var sophosIdentity = await GetWritablePartnerIdentityAsync(cancellationToken).ConfigureAwait(false);
        var path = $"{sophosIdentity.TenantsPath}/{Uri.EscapeDataString(request.Id)}";
        var uri = new Uri(globalApiUri, path);
        Trace?.Invoke($"Sophos Central PATCH {path}");
        using var response = await rateLimiter.SendAsync(
            httpClient,
            () => CreateTenantWriteRequest(
                HttpMethod.Patch,
                uri,
                sophosIdentity,
                new Dictionary<string, object?> { ["showAs"] = request.Name.Trim() },
                "application/merge-patch+json"),
            Trace,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonResponseAsync(response, "tenant update", cancellationToken).ConfigureAwait(false);
        var updated = MapTenant(document.RootElement);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "Customer",
            Id = updated.Id,
            Action = "Update",
            Success = true,
            Message = $"Updated Sophos Central tenant display name to '{updated.Name}'.",
            Raw = document.RootElement.Clone()
        };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var sophosIdentity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetTenantsPageAsync(sophosIdentity, 1, 1, true, cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetPropertyIgnoreCase("items", out var items) && items.ValueKind == JsonValueKind.Array;
    }

    public void Dispose()
    {
        authenticationGate.Dispose();
        rateLimiter.Dispose();
        httpClient.Dispose();
    }

    private async Task<SophosIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        if (identity != null && TokenIsCurrent()) return identity;

        await authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TokenIsCurrent()) await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            if (identity != null) return identity;

            Trace?.Invoke("Sophos Central GET whoami/v1");
            using var request = CreateAuthorizedRequest(HttpMethod.Get, new Uri(globalApiUri, "whoami/v1"));
            using var response = await rateLimiter.SendAsync(httpClient, () => CloneRequest(request), Trace, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonResponseAsync(response, "identity", cancellationToken).ConfigureAwait(false);
            var id = document.RootElement.GetString("id");
            var idType = document.RootElement.GetString("idType");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(idType))
                throw new InvalidOperationException("Sophos Central identity response did not include id and idType.");

            identity = idType.Trim().ToLowerInvariant() switch
            {
                "partner" => new SophosIdentity(id, "partner/v1/tenants", "X-Partner-ID"),
                "organization" => new SophosIdentity(id, "organization/v1/tenants", "X-Organization-ID"),
                _ => throw new InvalidOperationException($"Sophos Central identity type '{idType}' cannot enumerate customer tenants. Use partner or organization API credentials.")
            };
            return identity;
        }
        finally
        {
            authenticationGate.Release();
        }
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        Trace?.Invoke("Sophos Central POST OAuth token");
        using var response = await rateLimiter.SendAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Post, identityUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                    ["scope"] = "token"
                })
            },
            Trace,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonResponseAsync(response, "OAuth token", cancellationToken).ConfigureAwait(false);
        accessToken = document.RootElement.GetString("access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Sophos Central token response did not include an access token.");
        var expiresIn = document.RootElement.GetInt("expires_in") ?? 3600;
        if (expiresIn < 1) throw new InvalidOperationException("Sophos Central token response included an invalid expiry.");
        var refreshSkew = Math.Min(60, Math.Max(1, expiresIn / 10));
        accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, expiresIn - refreshSkew));
    }

    private async Task<JsonDocument> GetTenantsPageAsync(
        SophosIdentity sophosIdentity,
        int page,
        int pageSize,
        bool includePageTotal,
        CancellationToken cancellationToken)
    {
        var path = $"{sophosIdentity.TenantsPath}?page={page}&pageSize={pageSize}";
        if (includePageTotal) path += "&pageTotal=true";
        var uri = new Uri(globalApiUri, path);
        Trace?.Invoke($"Sophos Central GET {path}");
        using var response = await rateLimiter.SendAsync(
            httpClient,
            () =>
            {
                var request = CreateAuthorizedRequest(HttpMethod.Get, uri);
                request.Headers.Add(sophosIdentity.HeaderName, sophosIdentity.Id);
                return request;
            },
            Trace,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync(response, "tenants", cancellationToken).ConfigureAwait(false);
    }

    private async Task<SophosIdentity> GetWritablePartnerIdentityAsync(CancellationToken cancellationToken)
    {
        var sophosIdentity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!sophosIdentity.HeaderName.Equals("X-Partner-ID", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Sophos Central tenant writes require partner API credentials; organization credentials can enumerate tenants but cannot create or update them.");
        }

        return sophosIdentity;
    }

    private Dictionary<string, object?> CreateTenantPayload(EntityWriteRequest request)
    {
        var dataGeography = FieldString(request, "dataGeography") ?? NormalizeOptional(options.DefaultDataGeography);
        var dataRegion = FieldString(request, "dataRegion") ?? NormalizeOptional(options.DefaultDataRegion);
        if (dataGeography == null && dataRegion == null)
            throw new InvalidOperationException("Sophos Central tenant creation requires dataGeography or dataRegion in the source entity or connection defaults.");

        var billingType = FieldString(request, "billingType") ?? NormalizeOptional(options.DefaultBillingType);
        if (billingType == null)
            throw new InvalidOperationException("Sophos Central tenant creation requires billingType in the source entity or connection defaults.");

        var firstName = RequiredFieldString(request, "contactFirstName", "contact first name");
        var lastName = RequiredFieldString(request, "contactLastName", "contact last name");
        var email = RequiredFieldString(request, "contactEmail", "contact email");
        var address = RequiredAddress(request);
        var contact = new Dictionary<string, object?>
        {
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["email"] = email,
            ["address"] = address
        };
        var phone = FieldString(request, "contactPhone");
        if (phone != null) contact["phone"] = phone;

        var payload = new Dictionary<string, object?>
        {
            ["name"] = request.Name.Trim(),
            ["billingType"] = billingType,
            ["contact"] = contact
        };
        if (dataGeography != null) payload["dataGeography"] = dataGeography;
        if (dataRegion != null) payload["dataRegion"] = dataRegion;

        var products = FieldString(request, "products");
        if (products != null)
        {
            payload["products"] = products
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(code => new Dictionary<string, object?> { ["code"] = code })
                .ToArray();
        }

        var acceptedSampleSubmission = FieldString(request, "acceptedSampleSubmission");
        if (acceptedSampleSubmission != null)
        {
            if (!bool.TryParse(acceptedSampleSubmission, out var accepted))
                throw new InvalidOperationException("Sophos Central acceptedSampleSubmission must be true or false.");
            payload["acceptedSampleSubmission"] = accepted;
        }

        return payload;
    }

    private HttpRequestMessage CreateTenantWriteRequest(
        HttpMethod method,
        Uri uri,
        SophosIdentity sophosIdentity,
        IReadOnlyDictionary<string, object?> payload,
        string mediaType)
    {
        var request = CreateAuthorizedRequest(method, uri);
        request.Headers.Add(sophosIdentity.HeaderName, sophosIdentity.Id);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, mediaType);
        return request;
    }

    private static string RequiredFieldString(EntityWriteRequest request, string name, string description)
    {
        return FieldString(request, name)
            ?? throw new InvalidOperationException($"Sophos Central tenant creation requires {description}.");
    }

    private static string? FieldString(EntityWriteRequest request, string name)
    {
        if (!request.Fields.TryGetValue(name, out var value) || value == null) return null;
        return value switch
        {
            string text => NormalizeOptional(text),
            JsonElement element when element.ValueKind == JsonValueKind.String => NormalizeOptional(element.GetString()),
            _ => NormalizeOptional(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
        };
    }

    private static IReadOnlyDictionary<string, object?> RequiredAddress(EntityWriteRequest request)
    {
        if (!request.Fields.TryGetValue("address", out var value) || value is not IReadOnlyDictionary<string, object?> address)
            throw new InvalidOperationException("Sophos Central tenant creation requires a contact address.");

        foreach (var field in new[] { "address1", "city", "postalCode", "countryCode" })
        {
            if (!address.TryGetValue(field, out var fieldValue) || string.IsNullOrWhiteSpace(fieldValue as string))
                throw new InvalidOperationException($"Sophos Central tenant creation requires contact address {field}.");
        }

        return address
            .Where(pair => pair.Value is not string text || !string.IsNullOrWhiteSpace(text))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, Uri uri)
    {
        var token = accessToken ?? throw new InvalidOperationException("Sophos Central access token is unavailable.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private bool TokenIsCurrent()
    {
        return !string.IsNullOrWhiteSpace(accessToken) && DateTimeOffset.UtcNow < accessTokenExpiresAt;
    }

    private static ExternalEntity MapTenant(JsonElement item)
    {
        var id = item.GetString("id")
            ?? throw new InvalidOperationException("Sophos Central tenant did not include an id.");
        var name = item.GetString("showAs", "name")
            ?? throw new InvalidOperationException($"Sophos Central tenant '{id}' did not include a name.");
        var status = item.GetString("status");
        var entity = new ExternalEntity
        {
            Vendor = EntitySyncVendors.SophosCentral,
            EntityType = "Customer",
            Id = id,
            Name = name,
            IsActive = string.IsNullOrWhiteSpace(status) || status.Equals("active", StringComparison.OrdinalIgnoreCase)
        };
        entity.ExternalIds[TenantExternalIdName] = id;
        entity.CustomFields[TenantExternalIdName] = id;
        AddCustomField(entity, "SophosDataGeography", item.GetString("dataGeography"));
        AddCustomField(entity, "SophosDataRegion", item.GetString("dataRegion"));
        AddCustomField(entity, "SophosBillingType", item.GetString("billingType"));
        AddCustomField(entity, "SophosApiHost", item.GetString("apiHost"));
        AddCustomField(entity, "SophosStatus", status);
        AddCustomField(entity, "SophosManaged", item.GetString("managed"));
        AddCustomField(entity, "SophosPrimary", item.GetString("primary"));

        if (item.TryGetPropertyIgnoreCase("contact", out var contact) && contact.ValueKind == JsonValueKind.Object)
        {
            entity.Email = contact.GetString("email");
            entity.Phone = FirstNonEmpty(contact.GetString("phone"), contact.GetString("mobile"));
            if (contact.TryGetPropertyIgnoreCase("address", out var address) && address.ValueKind == JsonValueKind.Object)
            {
                entity.PrimaryAddress = new EntityAddress
                {
                    Attention = JoinName(contact.GetString("firstName"), contact.GetString("lastName")),
                    Line1 = address.GetString("address1", "line1"),
                    Line2 = address.GetString("address2", "line2"),
                    Line3 = address.GetString("address3", "line3"),
                    City = address.GetString("city"),
                    State = address.GetString("state", "county"),
                    PostalCode = address.GetString("postalCode"),
                    Country = address.GetString("countryCode", "country")
                };
            }
        }

        if (item.TryGetPropertyIgnoreCase("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            var codes = products.EnumerateArray()
                .Select(product => product.GetString("code"))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase);
            AddCustomField(entity, "SophosProducts", string.Join(',', codes));
        }

        return entity;
    }

    private static bool MatchesQuery(ExternalEntity entity, EntityQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Search)) return true;
        var search = query.Search.Trim();
        return entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entity.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (entity.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || entity.ExternalIds.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureCustomerEntityType(string entityType)
    {
        if (!entityType.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Sophos Central adapter supports EntityType Customer for partner or organization tenants.");
    }

    private static void AddCustomField(ExternalEntity entity, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) entity.CustomFields[name] = value;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string? JoinName(string? firstName, string? lastName)
    {
        var result = string.Join(' ', new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async Task<JsonDocument> ReadJsonResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Sophos Central {operation} request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response preview: {Preview(text)}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Sophos Central {operation} request returned non-JSON content. Response preview: {Preview(text)}",
                ex);
        }
    }

    private string Preview(string text)
    {
        foreach (var sensitiveValue in new[] { options.ClientId, options.ClientSecret, accessToken })
        {
            if (!string.IsNullOrWhiteSpace(sensitiveValue))
                text = text.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
        }

        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "...";
    }

    private static HttpClient CreateProductionClient(SophosCentralOptions options)
    {
        ValidateOptions(options);
        return VendorHttpClientFactory.Create();
    }

    private static HttpClient CreateTestClient(SophosCentralOptions options, HttpMessageHandler handler)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(handler);
        return VendorHttpClientFactory.Create(null, handler, minimumRequestInterval: TimeSpan.Zero);
    }

    private static void ValidateOptions(SophosCentralOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ClientId)) throw new ArgumentException("Sophos Central client ID is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientSecret)) throw new ArgumentException("Sophos Central client secret is required.", nameof(options));
        ValidateHttpsUri(options.IdentityUrl, "identity URL");
        ValidateHttpsUri(options.GlobalApiUrl, "global API URL");
    }

    private static void ValidateHttpsUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"Sophos Central {name} must be an absolute HTTPS URL without user info, query, or fragment.");
        }
    }

    private sealed record SophosIdentity(string Id, string TenantsPath, string HeaderName);
}
