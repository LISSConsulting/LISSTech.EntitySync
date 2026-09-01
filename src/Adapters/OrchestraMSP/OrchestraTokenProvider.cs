using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LISSTech.EntitySync.Adapters.OrchestraMSP;

public class OrchestraDependencyException : InvalidOperationException
{
    public OrchestraDependencyException(string safeCode, HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base($"OrchestraMSP dependency failed with code '{safeCode}'.", innerException)
    {
        if (string.IsNullOrWhiteSpace(safeCode))
            throw new ArgumentException("A safe dependency code is required.", nameof(safeCode));
        SafeCode = safeCode;
        StatusCode = statusCode;
    }

    public string SafeCode { get; }
    public HttpStatusCode? StatusCode { get; }
}

public sealed class StaleCanonicalVersionException : OrchestraDependencyException
{
    public StaleCanonicalVersionException()
        : base("CANONICAL_VERSION_CONFLICT", HttpStatusCode.Conflict)
    {
    }
}

public sealed class AmbiguousCanonicalWriteException : OrchestraDependencyException
{
    public AmbiguousCanonicalWriteException(Exception innerException)
        : base("ORCHESTRA_WRITE_OUTCOME_UNKNOWN", null, innerException)
    {
    }
}

public sealed class OrchestraTokenProvider : IDisposable
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly HttpClient httpClient;
    private readonly Uri tokenEndpoint;
    private readonly string clientId;
    private readonly byte[] clientSecret;
    private readonly string scope;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset refreshAt;
    private int disposed;

    public OrchestraTokenProvider(
        HttpClient httpClient,
        Uri authority,
        string tenantId,
        string clientId,
        ReadOnlySpan<byte> clientSecret,
        string resource,
        long generation,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateAuthority(authority);
        tenantId = Require(tenantId, nameof(tenantId));
        this.clientId = Require(clientId, nameof(clientId));
        if (clientSecret.IsEmpty)
            throw new ArgumentException("Client secret is required.", nameof(clientSecret));
        resource = Require(resource, nameof(resource)).TrimEnd('/');
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation), generation,
                "Connection generation must be positive.");

        this.httpClient = httpClient;
        this.clientSecret = clientSecret.ToArray();
        scope = resource + "/.default";
        Generation = generation;
        this.timeProvider = timeProvider;
        tokenEndpoint = new Uri(
            EnsureTrailingSlash(authority),
            Uri.EscapeDataString(tenantId) + "/oauth2/v2.0/token");
    }

    public long Generation { get; }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var cached = accessToken;
        if (cached is not null && timeProvider.GetUtcNow() < refreshAt) return cached;

        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cached = accessToken;
            if (cached is not null && timeProvider.GetUtcNow() < refreshAt) return cached;
            return await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        string? secret = null;
        try
        {
            secret = Encoding.UTF8.GetString(clientSecret);
            using var content = new FormUrlEncodedContent(
            [
                new("grant_type", "client_credentials"),
                new("client_id", clientId),
                new("client_secret", secret),
                new("scope", scope)
            ]);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = content
            };
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OrchestraDependencyException(
                    "ORCHESTRA_TOKEN_REJECTED", response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            TokenResponse? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<TokenResponse>(
                    stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new OrchestraDependencyException(
                    "ORCHESTRA_TOKEN_CONTRACT_INVALID", response.StatusCode, exception);
            }
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken)
                || payload.ExpiresIn <= 0)
                throw new OrchestraDependencyException(
                    "ORCHESTRA_TOKEN_CONTRACT_INVALID", response.StatusCode);

            var now = timeProvider.GetUtcNow();
            var lifetime = TimeSpan.FromSeconds(payload.ExpiresIn);
            accessToken = payload.AccessToken;
            refreshAt = now + (lifetime > RefreshSkew ? lifetime - RefreshSkew : TimeSpan.Zero);
            return accessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OrchestraDependencyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new OrchestraDependencyException(
                "ORCHESTRA_TOKEN_UNAVAILABLE", null, exception);
        }
        finally
        {
            secret = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        accessToken = null;
        refreshAt = default;
        CryptographicOperations.ZeroMemory(clientSecret);
        refreshGate.Dispose();
    }

    private static void ValidateAuthority(Uri authority)
    {
        if (!authority.IsAbsoluteUri
            || (!authority.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(authority.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     && IPAddress.TryParse(authority.Host, out var address)
                     && IPAddress.IsLoopback(address)))
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
            throw new ArgumentException("A safe HTTPS OAuth authority is required.", nameof(authority));
    }

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? value
            : new Uri(value.AbsoluteUri + "/", UriKind.Absolute);

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();

    private sealed class TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public long ExpiresIn { get; init; }
    }
}
