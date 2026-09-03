using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.NetSuite;

public sealed class NetSuiteEntityAdapter : IEntityAdapter, ISuiteQlExpertAdapter, IDisposable
{
    private const int SuiteQlPageSize = 1000;
    private readonly NetSuiteOptions options;
    private readonly HttpClient httpClient;
    private readonly RateLimitedHttpRequester rateLimiter = new("NetSuite");

    public NetSuiteEntityAdapter(NetSuiteOptions options)
    {
        this.options = options;
        httpClient = VendorHttpClientFactory.Create();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => "NetSuite";

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("NetSuite adapter currently supports EntityType Customer.");
        var suiteQl = BuildCustomerQuery(query);
        Trace?.Invoke($"NetSuite SuiteQL: {suiteQl}");
        var entities = new List<ExternalEntity>();
        await ReadSuiteQlPagesAsync(
            suiteQl,
            query.Count,
            item => entities.Add(MapCustomer(item)),
            cancellationToken).ConfigureAwait(false);
        await AddCustomerAddressesAsync(entities, cancellationToken).ConfigureAwait(false);
        return entities;
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Creating NetSuite customers is not implemented in this adapter. Use NetSuite as a source for this workflow.");
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Updating NetSuite customers is not implemented in this adapter. Use NetSuite as a source for this workflow.");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var uri = BuildSuiteQlUri(null, 0);
        using var response = await SendSuiteQlAsync(
            BuildCustomerQuery(new EntityQuery { EntityType = "Customer", Count = 1 }),
            uri,
            cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeSuiteQlAsync(
        string suiteQl,
        CancellationToken cancellationToken) =>
        InvokeSuiteQlCoreAsync(suiteQl, null, cancellationToken);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ISuiteQlExpertAdapter.InvokeSuiteQlAsync(
        string suiteQl,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        return InvokeSuiteQlCoreAsync(suiteQl, maximumRows, cancellationToken);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeSuiteQlCoreAsync(
        string suiteQl,
        int? maximumRows,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(suiteQl))
            throw new ArgumentException("SuiteQL query is required.", nameof(suiteQl));
        Trace?.Invoke($"NetSuite SuiteQL: {suiteQl}");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await ReadSuiteQlPagesAsync(
            suiteQl,
            maximumRows,
            item =>
            {
                if (item.ValueKind != JsonValueKind.Object) return;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.EnumerateObject())
                    row[property.Name] = ToNetSuiteValue(property.Value);
                rows.Add(row);
            },
            cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public void Dispose()
    {
        rateLimiter.Dispose();
        httpClient.Dispose();
    }

    private static object? ToNetSuiteValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var longValue) ? longValue : value.TryGetDecimal(out var decimalValue) ? decimalValue : value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    private static async Task<JsonDocument> ReadJsonResponseAsync(HttpResponseMessage response, Uri uri, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildFailureMessage(response, uri, text));
        }

        if (LooksLikeHtml(text))
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "<none>";
            throw new InvalidOperationException($"NetSuite REST Web Services returned HTML instead of JSON. This usually means the REST Web Services feature is disabled, the account host is wrong, or OAuth/role permissions are wrong. Content-Type: {contentType}. URI: {SanitizeUri(uri)}. Response preview: {Preview(text)}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "<none>";
            throw new InvalidOperationException($"NetSuite REST Web Services returned non-JSON content. Content-Type: {contentType}. URI: {SanitizeUri(uri)}. Response preview: {Preview(text)}", ex);
        }
    }

    private static bool LooksLikeHtml(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<", StringComparison.OrdinalIgnoreCase);
    }

    private static string Preview(string text)
    {
        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "...";
    }

    private static string SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Query = string.Empty };
        return builder.Uri.ToString();
    }

    private static string BuildFailureMessage(HttpResponseMessage response, Uri uri, string text)
    {
        var message = $"NetSuite REST Web Services request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. URI: {SanitizeUri(uri)}.";
        var detail = TryReadNetSuiteErrorDetail(text);
        if (!string.IsNullOrWhiteSpace(detail) && detail.Contains("current role does not have permission", StringComparison.OrdinalIgnoreCase))
        {
            message += " NetSuite says the token role cannot perform the SuiteQL query. Confirm the integration token is assigned to the intended role and that role has REST Web Services access, SuiteAnalytics/SuiteQL access, and Customer record/list read permissions.";
        }

        return message + $" Response preview: {Preview(text)}";
    }

    private static string? TryReadNetSuiteErrorDetail(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetPropertyIgnoreCase("o:errorDetails", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    var value = detail.GetString("detail");
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private Uri BuildSuiteQlUri(int? limit, int offset)
    {
        var path = "/services/rest/query/v1/suiteql";
        var builder = !string.IsNullOrWhiteSpace(options.BaseUrl)
            ? new UriBuilder(new Uri(options.BaseUrl.TrimEnd('/'))) { Path = path }
            : new UriBuilder(Uri.UriSchemeHttps, $"{AccountHost(options.AccountId)}.suitetalk.api.netsuite.com") { Path = path };
        if (limit.HasValue) builder.Query = $"limit={limit.Value}&offset={offset}";
        return builder.Uri;
    }

    internal static string BuildCustomerQuery(EntityQuery query)
    {
        var fields = string.Join(", ",
            "id",
            "entityid",
            "companyname",
            "email",
            "phone",
            "url",
            "entitystatus",
            "BUILTIN.DF(entitystatus) AS entitystatus_display",
            "category",
            "BUILTIN.DF(category) AS category_display",
            "isinactive",
            "datecreated",
            "lastmodifieddate");
        var sql = new StringBuilder($"SELECT {fields} FROM customer");
        var filters = new List<string>();
        if (!query.IncludeInactive) filters.Add("isinactive = 'F'");
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = EscapeSuiteQlLikeLiteral(query.Search.ToLowerInvariant());
            filters.Add($"(LOWER(entityid) LIKE '%{search}%' ESCAPE '\\' OR LOWER(companyname) LIKE '%{search}%' ESCAPE '\\' OR LOWER(email) LIKE '%{search}%' ESCAPE '\\')");
        }

        if (filters.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", filters));
        sql.Append(" ORDER BY entityid, id");
        if (query.Count.HasValue) sql.Append(" FETCH FIRST ").Append(Math.Max(1, query.Count.Value)).Append(" ROWS ONLY");
        return sql.ToString();
    }

    internal static string EscapeSuiteQlLikeLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
    }

    private static string AccountHost(string accountId)
    {
        return accountId.Trim().ToLowerInvariant().Replace('_', '-');
    }

    private async Task AddCustomerAddressesAsync(List<ExternalEntity> entities, CancellationToken cancellationToken)
    {
        var ids = entities.Select(entity => entity.Id).Where(id => long.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return;

        var addressRows = new Dictionary<string, List<CustomerAddressRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Chunk(500))
        {
            var suiteQl = BuildCustomerAddressQuery(batch);
            Trace?.Invoke($"NetSuite SuiteQL: {suiteQl}");
            await ReadSuiteQlPagesAsync(
                suiteQl,
                null,
                item =>
                {
                    var customerId = item.GetString("customerid", "customerId", "entity");
                    if (string.IsNullOrWhiteSpace(customerId)) return;
                    var address = new EntityAddress
                    {
                        Attention = item.GetString("attention"),
                        Line1 = item.GetString("addr1"),
                        Line2 = item.GetString("addr2"),
                        Line3 = item.GetString("addr3"),
                        City = item.GetString("city"),
                        State = item.GetString("state"),
                        PostalCode = item.GetString("zip", "postalcode"),
                        Country = item.GetString("country")
                    };
                    if (IsAddressEmpty(address)) return;
                    if (!addressRows.TryGetValue(customerId, out var rows))
                    {
                        rows = new List<CustomerAddressRow>();
                        addressRows[customerId] = rows;
                    }
                    rows.Add(new CustomerAddressRow(address, ReadNetSuiteBool(item, "defaultbilling"), ReadNetSuiteBool(item, "defaultshipping")));
                },
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var entity in entities)
        {
            if (!addressRows.TryGetValue(entity.Id, out var rows) || rows.Count == 0) continue;
            entity.BillingAddress = rows.FirstOrDefault(row => row.IsDefaultBilling)?.Address;
            entity.ShippingAddress = rows.FirstOrDefault(row => row.IsDefaultShipping)?.Address;
            entity.PrimaryAddress = entity.BillingAddress ?? entity.ShippingAddress ?? rows[0].Address;
        }
    }

    private async Task ReadSuiteQlPagesAsync(
        string suiteQl,
        int? maximumItems,
        Action<JsonElement> consume,
        CancellationToken cancellationToken)
    {
        var itemLimit = maximumItems.HasValue ? Math.Max(1, maximumItems.Value) : int.MaxValue;
        var consumed = 0;
        var offset = 0;
        int? expectedTotalResults = null;
        while (consumed < itemLimit)
        {
            var remaining = itemLimit - consumed;
            var uri = BuildSuiteQlUri(SuiteQlPageSize, offset);
            using var response = await SendSuiteQlAsync(suiteQl, uri, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonResponseAsync(response, uri, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                ConsumeSuiteQlItems(root, remaining, consume);
                return;
            }
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"NetSuite REST Web Services returned JSON, but not an array or an object with an 'items' array. Root type: {root.ValueKind}.");
            }

            var pageCount = items.GetArrayLength();
            var countPresent = root.TryGetProperty("count", out var countProperty);
            var offsetPresent = root.TryGetProperty("offset", out var offsetProperty);
            var totalResultsPresent = root.TryGetProperty("totalResults", out var totalResultsProperty);
            var hasMorePresent = root.TryGetProperty("hasMore", out var hasMoreProperty);
            var metadataFields = (countPresent ? 1 : 0) + (offsetPresent ? 1 : 0) + (totalResultsPresent ? 1 : 0) + (hasMorePresent ? 1 : 0);
            if (metadataFields == 0)
            {
                if (pageCount >= SuiteQlPageSize)
                {
                    throw InvalidSuiteQlPage("a full page omitted count, offset, totalResults, and hasMore.");
                }
                ConsumeSuiteQlItems(items, remaining, consume);
                return;
            }
            if (metadataFields != 4
                || !TryReadNonNegativeInt(countProperty, out var reportedCount)
                || !TryReadNonNegativeInt(offsetProperty, out var reportedOffset)
                || !TryReadNonNegativeInt(totalResultsProperty, out var totalResults)
                || !TryReadBoolean(hasMoreProperty, out var hasMore))
            {
                throw InvalidSuiteQlPage("count, offset, totalResults, and hasMore must all be present with valid values.");
            }
            if (reportedCount != pageCount)
            {
                throw InvalidSuiteQlPage($"count {reportedCount} does not equal items length {pageCount}.");
            }
            if (reportedOffset != offset)
            {
                throw InvalidSuiteQlPage($"offset {reportedOffset} does not equal requested offset {offset}.");
            }
            if (pageCount > SuiteQlPageSize)
            {
                throw InvalidSuiteQlPage($"items length {pageCount} exceeds requested limit {SuiteQlPageSize}.");
            }
            if (expectedTotalResults.HasValue && expectedTotalResults.Value != totalResults)
            {
                throw InvalidSuiteQlPage($"totalResults changed from {expectedTotalResults.Value} to {totalResults}.");
            }
            expectedTotalResults ??= totalResults;
            var nextOffsetValue = (long)reportedOffset + reportedCount;
            if (nextOffsetValue > totalResults)
            {
                throw InvalidSuiteQlPage($"offset plus count {nextOffsetValue} exceeds totalResults {totalResults}.");
            }
            var nextOffset = (int)nextOffsetValue;
            var expectedHasMore = nextOffset < totalResults;
            if (hasMore != expectedHasMore)
            {
                throw InvalidSuiteQlPage($"hasMore is {hasMore}, but offset plus count is {nextOffset} and totalResults is {totalResults}.");
            }
            if (hasMore && pageCount == 0)
            {
                throw InvalidSuiteQlPage("hasMore is true on an empty page, so pagination cannot advance.");
            }
            ConsumeSuiteQlItems(items, remaining, consume);
            consumed += pageCount;
            if (consumed >= itemLimit || !hasMore) return;
            offset = nextOffset;
        }
    }

    private Task<HttpResponseMessage> SendSuiteQlAsync(string suiteQl, Uri uri, CancellationToken cancellationToken)
    {
        return rateLimiter.SendAsync(
            httpClient,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", BuildOAuthHeader("POST", uri));
                request.Headers.TryAddWithoutValidation("Prefer", "transient");
                request.Content = new StringContent(JsonSerializer.Serialize(new { q = suiteQl }), Encoding.UTF8, "application/json");
                return request;
            },
            Trace,
            cancellationToken);
    }

    private static void ConsumeSuiteQlItems(JsonElement items, int maximumItems, Action<JsonElement> consume)
    {
        var consumed = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (consumed >= maximumItems) return;
            consume(item);
            consumed++;
        }
    }

    private static bool TryReadNonNegativeInt(JsonElement property, out int value)
    {
        value = 0;
        return property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryReadBoolean(JsonElement property, out bool value)
    {
        value = false;
        if (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False) return false;
        value = property.GetBoolean();
        return true;
    }

    private static InvalidOperationException InvalidSuiteQlPage(string detail)
    {
        return new InvalidOperationException($"NetSuite SuiteQL pagination metadata is inconsistent: {detail}");
    }

    private static string BuildCustomerAddressQuery(IEnumerable<string> customerIds)
    {
        var idList = string.Join(", ", customerIds);
        return "SELECT ab.entity AS customerid, ab.defaultbilling, ab.defaultshipping, ea.attention, ea.addr1, ea.addr2, ea.addr3, ea.city, ea.state, ea.zip, BUILTIN.DF(ea.country) AS country "
            + "FROM customerAddressbook ab "
            + "JOIN customerAddressbookEntityAddress ea ON ea.nkey = ab.addressbookaddress "
            + $"WHERE ab.entity IN ({idList}) "
            + "ORDER BY ab.entity, ab.internalid";
    }

    private static bool ReadNetSuiteBool(JsonElement item, string name)
    {
        var value = item.GetString(name);
        return value != null && (value.Equals("T", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAddressEmpty(EntityAddress? address)
    {
        return address == null || string.IsNullOrWhiteSpace(address.Compact());
    }

    private ExternalEntity MapCustomer(JsonElement item)
    {
        var id = item.GetString("internalId", "id") ?? string.Empty;
        var email = item.GetString("email", "emailAddress");
        var website = item.GetString("website", "url");
        var isInactive = item.GetBool("isInactive", "isinactive") ?? string.Equals(item.GetString("isinactive"), "T", StringComparison.OrdinalIgnoreCase);
        var entity = new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Customer",
            Id = id,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NetSuiteInternalId"] = id },
            Name = item.GetString("customerName", "companyname", "entityId", "entityid", "name") ?? string.Empty,
            Email = email,
            Phone = item.GetString("phoneNumber", "phone"),
            Website = website,
            Domain = EntityNormalizer.NormalizeDomain(website, email),
            IsActive = !isInactive,
            CreatedAt = item.GetDate("createDate", "dateCreated", "datecreated"),
            UpdatedAt = item.GetDate("modifyDate", "lastModifiedDate", "lastmodifieddate"),
            PrimaryAddress = new EntityAddress
            {
                Attention = item.GetString("attention"),
                Line1 = item.GetString("address1", "addr1"),
                Line2 = item.GetString("address2", "addr2"),
                Line3 = item.GetString("address3", "addr3"),
                City = item.GetString("city"),
                State = item.GetString("state"),
                PostalCode = item.GetString("zip", "postalCode"),
                Country = item.GetString("country")
            }
        };
        AddCustomField(entity, "NetSuiteEntityStatus", item.GetString("entitystatus"));
        AddCustomField(entity, "NetSuiteEntityStatusDisplay", item.GetString("entitystatus_display"));
        AddCustomField(entity, "NetSuiteCategory", item.GetString("category"));
        AddCustomField(entity, "NetSuiteCategoryDisplay", item.GetString("category_display"));
        AddCustomField(entity, "NetSuiteCustomerName", entity.Name);
        return entity;
    }

    private static void AddCustomField(ExternalEntity entity, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) entity.CustomFields[name] = value;
    }

    private string BuildOAuthHeader(string method, Uri uri)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', 'A').Replace('/', 'B');
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = options.ConsumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA256",
            ["oauth_timestamp"] = timestamp,
            ["oauth_token"] = options.TokenId,
            ["oauth_version"] = "1.0"
        };

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                parameters[Uri.UnescapeDataString(pair[0])] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
        }

        var normalizedUrl = uri.GetLeftPart(UriPartial.Path);
        var parameterString = string.Join("&", parameters.Select(p => PercentEncode(p.Key) + "=" + PercentEncode(p.Value)));
        var signatureBase = method.ToUpperInvariant() + "&" + PercentEncode(normalizedUrl) + "&" + PercentEncode(parameterString);
        var signingKey = PercentEncode(options.ConsumerSecret) + "&" + PercentEncode(options.TokenSecret);
        using var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(signingKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBase)));

        var oauthParts = new Dictionary<string, string>
        {
            ["realm"] = options.AccountId,
            ["oauth_consumer_key"] = options.ConsumerKey,
            ["oauth_token"] = options.TokenId,
            ["oauth_nonce"] = nonce,
            ["oauth_timestamp"] = timestamp,
            ["oauth_signature_method"] = "HMAC-SHA256",
            ["oauth_version"] = "1.0",
            ["oauth_signature"] = signature
        };
        return string.Join(", ", oauthParts.Select(p => p.Key + "=\"" + PercentEncode(p.Value) + "\""));
    }

    private static string PercentEncode(string value)
    {
        return Uri.EscapeDataString(value ?? string.Empty).Replace("+", "%20").Replace("*", "%2A").Replace("%7E", "~");
    }

    private sealed record CustomerAddressRow(EntityAddress Address, bool IsDefaultBilling, bool IsDefaultShipping);
}
