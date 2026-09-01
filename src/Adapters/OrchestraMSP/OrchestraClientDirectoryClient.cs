using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LISSTech.EntitySync.Adapters.OrchestraMSP;

public sealed record OrchestraPlatformLinkCommand(
    string PlatformInstanceId,
    string Platform,
    string ExternalId,
    string Status,
    string EntityType,
    Guid EntityId);

public sealed class OrchestraClientDirectoryClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumCursorLength = 2048;
    private static readonly Regex OpaqueCursorPattern = new(
        "^[A-Za-z0-9_-]+={0,2}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;
    private readonly OrchestraTokenProvider tokenProvider;
    private readonly Uri baseUri;
    private readonly int maximumPages;
    private readonly bool disposeHttpClient;

    public OrchestraClientDirectoryClient(
        HttpClient httpClient,
        OrchestraTokenProvider tokenProvider,
        Uri baseUri,
        int maximumPages = 100,
        bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(baseUri);
        ValidateBaseUri(baseUri);
        if (maximumPages is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumPages), maximumPages,
                "Maximum pages must be between 1 and 1000.");
        this.httpClient = httpClient;
        this.tokenProvider = tokenProvider;
        this.baseUri = baseUri;
        this.maximumPages = maximumPages;
        this.disposeHttpClient = disposeHttpClient;
    }

    internal async Task<IReadOnlyList<OrchestraClientContract>> ListClientsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var result = new List<OrchestraClientContract>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < maximumPages; pageNumber++)
        {
            var uri = BuildClientsUri(includeInactive, cursor);
            var page = await GetAsync<OrchestraClientPage>(
                uri, allowNotFound: false, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
            if (page.Items is null)
                throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
            result.AddRange(page.Items);
            if (string.IsNullOrEmpty(page.NextCursor)) return result;
            cursor = ValidateCursor(page.NextCursor);
            if (!seenCursors.Add(cursor))
                throw new OrchestraDependencyException("ORCHESTRA_CURSOR_INVALID");
        }
        throw new OrchestraDependencyException("ORCHESTRA_PAGE_LIMIT_EXCEEDED");
    }

    internal Task<OrchestraClientContract?> ReadClientAsync(
        Guid clientId,
        CancellationToken cancellationToken) =>
        GetAsync<OrchestraClientContract>(
            BuildRelativeUri("clients/" + clientId.ToString("D")),
            allowNotFound: true,
            cancellationToken);

    internal async Task<JsonElement> SendWriteAsync(
        HttpMethod method,
        string relativePath,
        object payload,
        string idempotencyKey,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(payload);
        idempotencyKey = Require(idempotencyKey, nameof(idempotencyKey));
        if (idempotencyKey.Length > 256)
            throw new ArgumentException("Idempotency key cannot exceed 256 characters.",
                nameof(idempotencyKey));
        if (expectedVersion is <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var uri = BuildRelativeUri(relativePath);

        try
        {
            return await SendWriteOnceAsync(
                method, uri, body, idempotencyKey, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StaleCanonicalVersionException)
        {
            throw;
        }
        catch (Exception first) when (first is HttpRequestException or IOException)
        {
            try
            {
                return await SendWriteOnceAsync(
                    method, uri, body, idempotencyKey, expectedVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (StaleCanonicalVersionException)
            {
                throw;
            }
            catch (Exception second) when (
                second is HttpRequestException or IOException or OrchestraDependencyException)
            {
                throw new AmbiguousCanonicalWriteException(second);
            }
        }
    }

    internal async Task<OrchestraPlatformLinkContract> UpsertPlatformLinkAsync(
        OrchestraPlatformLinkCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLinkCommand(command);
        var json = await SendWriteAsync(
            HttpMethod.Put,
            "platform-links",
            command,
            idempotencyKey,
            expectedVersion: null,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return json.Deserialize<OrchestraPlatformLinkContract>(JsonOptions)
                ?? throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        }
        catch (JsonException exception)
        {
            throw new OrchestraDependencyException(
                "ORCHESTRA_CONTRACT_INVALID", null, exception);
        }
    }

    private async Task<JsonElement> SendWriteOnceAsync(
        HttpMethod method,
        Uri uri,
        byte[] body,
        string idempotencyKey,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        using var request = await CreateAuthorizedRequestAsync(
            method, uri, cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (expectedVersion.HasValue)
            request.Headers.TryAddWithoutValidation(
                "If-Match", expectedVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new StaleCanonicalVersionException();
        EnsureSuccess(response);
        return await ReadJsonElementAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(
        Uri uri,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new OrchestraDependencyException(
                "ORCHESTRA_CONTRACT_INVALID", response.StatusCode, exception);
        }
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonElement> ReadJsonElementAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new IOException(
                "The OrchestraMSP write response was unavailable for authoritative readback.",
                exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "ORCHESTRA_AUTHENTICATION_FAILED",
            HttpStatusCode.Forbidden => "ORCHESTRA_AUTHORIZATION_FAILED",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                "ORCHESTRA_VALIDATION_FAILED",
            HttpStatusCode.NotFound => "ORCHESTRA_NOT_FOUND",
            _ when (int)response.StatusCode >= 500 => "ORCHESTRA_UNAVAILABLE",
            _ => "ORCHESTRA_REQUEST_REJECTED"
        };
        throw new OrchestraDependencyException(code, response.StatusCode);
    }

    private Uri BuildClientsUri(bool includeInactive, string? cursor)
    {
        var query = new StringBuilder("include_inactive=")
            .Append(includeInactive ? "true" : "false")
            .Append("&limit=")
            .Append(PageSize);
        if (cursor is not null)
            query.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        var builder = new UriBuilder(BuildRelativeUri("clients")) { Query = query.ToString() };
        return builder.Uri;
    }

    private Uri BuildRelativeUri(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Uri.TryCreate(relativePath, UriKind.Absolute, out _)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A safe relative Client Directory path is required.",
                nameof(relativePath));
        var result = new Uri(baseUri, relativePath);
        if (!result.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !result.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || result.Port != baseUri.Port
            || !result.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new OrchestraDependencyException("ORCHESTRA_URI_INVALID");
        return result;
    }

    private static string ValidateCursor(string cursor)
    {
        if (cursor.Length is 0 or > MaximumCursorLength
            || !OpaqueCursorPattern.IsMatch(cursor))
            throw new OrchestraDependencyException("ORCHESTRA_CURSOR_INVALID");
        return cursor;
    }

    private static void ValidateBaseUri(Uri value)
    {
        const string requiredPath = "/api/v1/internal/client-directory/";
        if (!value.IsAbsoluteUri
            || (!value.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(value.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     && IPAddress.TryParse(value.Host, out var address)
                     && IPAddress.IsLoopback(address)))
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || !value.AbsolutePath.Equals(requiredPath, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Client Directory base URI must end with '{requiredPath}'.", nameof(value));
    }

    private static void ValidateLinkCommand(OrchestraPlatformLinkCommand command)
    {
        _ = Require(command.PlatformInstanceId, nameof(command.PlatformInstanceId));
        _ = Require(command.Platform, nameof(command.Platform));
        _ = Require(command.ExternalId, nameof(command.ExternalId));
        _ = Require(command.Status, nameof(command.Status));
        _ = Require(command.EntityType, nameof(command.EntityType));
        if (command.EntityId == Guid.Empty)
            throw new ArgumentException("Platform link entity ID is required.", nameof(command));
    }

    public void Dispose()
    {
        tokenProvider.Dispose();
        if (disposeHttpClient) httpClient.Dispose();
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}

internal sealed class OrchestraClientPage
{
    public List<OrchestraClientContract>? Items { get; init; }
    public string? NextCursor { get; init; }
}

internal sealed class OrchestraClientContract
{
    public Guid ClientId { get; init; }
    public long Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string LifecycleStatus { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public Guid? MergedIntoClientId { get; init; }
    public List<Guid> MergedFromClientIds { get; init; } = [];
    public Dictionary<string, JsonElement> Fields { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public List<OrchestraSiteContract> Sites { get; init; } = [];
    public List<OrchestraAddressContract> Addresses { get; init; } = [];
    public List<OrchestraPlatformLinkContract> PlatformLinks { get; init; } = [];
}

internal sealed class OrchestraSiteContract
{
    public Guid SiteId { get; init; }
    public Guid ClientId { get; init; }
    public long Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string LifecycleStatus { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public Dictionary<string, JsonElement> Fields { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public List<OrchestraAddressContract> Addresses { get; init; } = [];
    public List<OrchestraPlatformLinkContract> PlatformLinks { get; init; } = [];
}

internal sealed class OrchestraAddressContract
{
    public Guid AddressId { get; init; }
    public Guid ClientId { get; init; }
    public Guid? SiteId { get; init; }
    public long Version { get; init; }
    public string AddressType { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public string? Attention { get; init; }
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? Line3 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
    public Dictionary<string, JsonElement> Fields { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public List<OrchestraPlatformLinkContract> PlatformLinks { get; init; } = [];
}

internal sealed class OrchestraPlatformLinkContract
{
    public string PlatformInstanceId { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
}
