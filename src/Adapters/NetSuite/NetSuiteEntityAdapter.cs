using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.NetSuite;

public sealed class NetSuiteEntityAdapter : IEntityAdapter, IDisposable
{
    private readonly NetSuiteOptions options;
    private readonly HttpClient httpClient = new();

    public NetSuiteEntityAdapter(NetSuiteOptions options)
    {
        this.options = options;
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => "NetSuite";

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("NetSuite adapter currently supports EntityType Customer.");
        var uri = BuildUri(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", BuildOAuthHeader("GET", uri));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) ? items : root;
        var entities = new List<ExternalEntity>();
        foreach (var item in array.EnumerateArray()) entities.Add(MapCustomer(item));
        return entities;
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Creating NetSuite customers is not implemented in this adapter. Use NetSuite as a source for this workflow.");
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Updating NetSuite customers is not implemented in this adapter. Use NetSuite as a source for this workflow.");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var uri = BuildUri(new EntityQuery { EntityType = "Customer", Count = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", BuildOAuthHeader("GET", uri));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => httpClient.Dispose();

    private Uri BuildUri(EntityQuery query)
    {
        var builder = new UriBuilder(options.RestletUrl);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search)) parts.Add("search=" + Uri.EscapeDataString(query.Search));
        if (query.Count.HasValue) parts.Add("count=" + query.Count.Value);
        if (query.IncludeInactive) parts.Add("includeInactive=true");
        if (!string.IsNullOrWhiteSpace(builder.Query)) parts.Insert(0, builder.Query.TrimStart('?'));
        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    private ExternalEntity MapCustomer(JsonElement item)
    {
        var id = item.GetString("internalId", "id") ?? string.Empty;
        var email = item.GetString("email", "emailAddress");
        var website = item.GetString("website", "url");
        return new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Customer",
            Id = id,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NetSuiteInternalId"] = id },
            Name = item.GetString("customerName", "entityId", "name") ?? string.Empty,
            Email = email,
            Phone = item.GetString("phoneNumber", "phone"),
            Website = website,
            Domain = EntityNormalizer.NormalizeDomain(website, email),
            IsActive = !(item.GetBool("isInactive") ?? false),
            CreatedAt = item.GetDate("createDate", "dateCreated"),
            UpdatedAt = item.GetDate("modifyDate", "lastModifiedDate"),
            PrimaryAddress = new EntityAddress
            {
                Line1 = item.GetString("address1", "addr1"),
                Line2 = item.GetString("address2", "addr2"),
                Line3 = item.GetString("address3", "addr3"),
                City = item.GetString("city"),
                State = item.GetString("state"),
                PostalCode = item.GetString("zip", "postalCode"),
                Country = item.GetString("country")
            }
        };
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
}
