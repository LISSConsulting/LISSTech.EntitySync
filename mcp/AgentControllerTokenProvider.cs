using System.Text;
using System.Text.Json;

namespace LISSTech.EntitySync.Mcp;

/// <summary>
/// Server-managed AgentController service-principal token provider used by the MCP
/// host and any other authenticated boot path that does not own an interactive
/// operator session. It obtains a Microsoft Entra ID client_credentials access
/// token, exchanges it at the AgentController operator-token endpoint, and keeps
/// the resulting short-lived token in memory only. Errors raised by this provider
/// never include the configured client secret or any access token.
/// </summary>
internal sealed class AgentControllerTokenProvider : IDisposable
{
    internal const string DefaultInternalScope = "customer_scope_sync:write";
    internal const string DefaultExchangePath = "v1/operator-token/exchange";

    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient httpClient;
    private readonly AgentControllerProviderConfiguration configuration;
    private readonly bool disposeHttpClient;

    internal AgentControllerTokenProvider(AgentControllerProviderConfiguration configuration)
        : this(configuration, SharedHttpClient, disposeHttpClient: false)
    {
    }

    internal AgentControllerTokenProvider(
        AgentControllerProviderConfiguration configuration,
        HttpMessageHandler handler)
        : this(
            configuration,
            new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true),
            disposeHttpClient: true)
    {
    }

    private AgentControllerTokenProvider(
        AgentControllerProviderConfiguration configuration,
        HttpClient httpClient,
        bool disposeHttpClient)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        this.configuration = configuration.Clone();
        this.httpClient = httpClient;
        this.disposeHttpClient = disposeHttpClient;
    }

    /// <summary>
    /// Reads server-managed configuration from the documented
    /// <c>AGENTCONTROLLER_*</c> environment variables. Missing or malformed
    /// values raise <see cref="InvalidOperationException"/> without disclosing
    /// configured secrets. The provider is created without an initial token;
    /// callers should invoke <see cref="AcquireAsync"/> before constructing the
    /// adapter.
    /// </summary>
    internal static AgentControllerTokenProvider FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new AgentControllerTokenProvider(AgentControllerProviderConfiguration.FromEnvironment(environment));
    }

    /// <summary>
    /// Performs the Entra client_credentials request and AgentController
    /// token exchange. The returned <see cref="AgentControllerTokenExchange"/>
    /// carries the LTAC bearer access token, its declared expires_in, and the
    /// operator operations base URL the adapter must target. The token is
    /// intentionally not cached; callers that need to re-exchange after a 401/403
    /// should invoke <see cref="AcquireAsync"/> again.
    /// </summary>
    internal async Task<AgentControllerTokenExchange> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entraAccessToken = await RequestEntraAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await ExchangeOperatorTokenAsync(entraAccessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshot of the server-managed configuration. The client secret is omitted.</summary>
    internal AgentControllerProviderConfiguration Configuration => configuration.RedactedClone();

    private async Task<string> RequestEntraAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tokenEndpoint = new Uri($"https://login.microsoftonline.com/{configuration.EntraTenantId}/oauth2/v2.0/token", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = configuration.EntraClientId,
                ["client_secret"] = configuration.EntraClientSecret,
                ["scope"] = configuration.EntraScope
            })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AgentController token request failed: entra token endpoint returned HTTP {(int)response.StatusCode}.");
        }

        return ParseEntraAccessToken(body);
    }

    private async Task<AgentControllerTokenExchange> ExchangeOperatorTokenAsync(string entraAccessToken, CancellationToken cancellationToken)
    {
        var exchangeUri = BuildExchangeUri();
        using var request = new HttpRequestMessage(HttpMethod.Post, exchangeUri)
        {
            Content = new StringContent(
                BuildExchangePayload(entraAccessToken),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AgentController token request failed: operator-token exchange returned HTTP {(int)response.StatusCode}.");
        }

        return ParseExchangeResponse(body, configuration.InternalScope);
    }

    private Uri BuildExchangeUri()
    {
        var baseUri = new Uri(
            configuration.AuthBaseUrl.EndsWith("/", StringComparison.Ordinal)
                ? configuration.AuthBaseUrl
                : configuration.AuthBaseUrl + "/",
            UriKind.Absolute);
        return new Uri(baseUri, configuration.ExchangePath.TrimStart('/'));
    }

    private static string ParseEntraAccessToken(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("AgentController token request failed: entra token endpoint returned an empty body.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
            {
                throw new InvalidOperationException("AgentController token request failed: entra token endpoint response did not include access_token.");
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("AgentController token request failed: entra token endpoint returned an empty access_token.");
            }

            return token;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("AgentController token request failed: entra token endpoint returned a malformed response.");
        }
    }

    private static string BuildExchangePayload(string entraAccessToken)
    {
        return JsonSerializer.Serialize(new
        {
            entra_access_token = entraAccessToken,
            requested_customer_slugs = Array.Empty<string>()
        });
    }

    private static AgentControllerTokenExchange ParseExchangeResponse(string body, string internalScope)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("AgentController token request failed: operator-token exchange returned an empty body.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var accessTokenElement))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange response did not include access_token.");
            }

            var accessToken = accessTokenElement.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange returned an empty access_token.");
            }

            if (!root.TryGetProperty("token_type", out var tokenTypeElement)
                || !string.Equals(tokenTypeElement.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange response did not declare Bearer token_type.");
            }

            if (!root.TryGetProperty("ops_base_url", out var opsElement))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange response did not include ops_base_url.");
            }

            var opsValue = opsElement.GetString();
            if (string.IsNullOrWhiteSpace(opsValue)
                || !Uri.TryCreate(opsValue, UriKind.Absolute, out var opsUri)
                || !opsUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(opsUri.UserInfo)
                || !string.IsNullOrEmpty(opsUri.Query)
                || !string.IsNullOrEmpty(opsUri.Fragment))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange ops_base_url must be an absolute HTTPS URL without user info, a query, or a fragment.");
            }

            if (!root.TryGetProperty("expires_in", out var expiresElement)
                || !expiresElement.TryGetInt32(out var expiresIn)
                || expiresIn <= 0)
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange response did not include a positive expires_in.");
            }

            if (!root.TryGetProperty("scope", out var scopeElement)
                || scopeElement.ValueKind != JsonValueKind.String
                || !scopeElement.GetString()!
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SequenceEqual([internalScope], StringComparer.Ordinal))
            {
                throw new InvalidOperationException("AgentController token request failed: operator-token exchange did not grant exactly the required EntitySync scope.");
            }

            return new AgentControllerTokenExchange(accessToken, expiresIn, opsUri, internalScope);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("AgentController token request failed: operator-token exchange returned a malformed response.");
        }
    }

    public void Dispose()
    {
        if (disposeHttpClient)
        {
            httpClient.Dispose();
        }
    }
}

/// <summary>
/// Server-managed AgentController service-principal configuration. The client
/// secret is never serialized and never appears in <c>ToString</c> output.
/// </summary>
internal sealed class AgentControllerProviderConfiguration
{
    public AgentControllerProviderConfiguration(
        string authBaseUrl,
        string entraTenantId,
        string entraClientId,
        string entraClientSecret,
        string entraScope,
        string internalScope,
        string exchangePath)
    {
        AuthBaseUrl = authBaseUrl;
        EntraTenantId = entraTenantId;
        EntraClientId = entraClientId;
        EntraClientSecret = entraClientSecret;
        EntraScope = entraScope;
        InternalScope = internalScope;
        ExchangePath = exchangePath;
    }

    internal string AuthBaseUrl { get; }
    internal string EntraTenantId { get; }
    internal string EntraClientId { get; }
    internal string EntraScope { get; }
    internal string InternalScope { get; }
    internal string ExchangePath { get; }

    /// <summary>The configured Entra client secret. Never serialized or logged.</summary>
    internal string EntraClientSecret { get; }

    internal void Validate()
    {
        EnsureAbsoluteHttpsUrl(AuthBaseUrl, nameof(AuthBaseUrl));
        EnsureGuid(EntraTenantId, nameof(EntraTenantId));
        EnsureGuid(EntraClientId, nameof(EntraClientId));
        RequireValue(EntraClientSecret, nameof(EntraClientSecret));
        EnsureDefaultScope(EntraScope, nameof(EntraScope));
        RequireValue(InternalScope, nameof(InternalScope));
        RequireValue(ExchangePath, nameof(ExchangePath));
    }

    internal AgentControllerProviderConfiguration Clone()
    {
        return new AgentControllerProviderConfiguration(
            AuthBaseUrl,
            EntraTenantId,
            EntraClientId,
            EntraClientSecret,
            EntraScope,
            InternalScope,
            ExchangePath);
    }

    internal AgentControllerProviderConfiguration RedactedClone()
    {
        return new AgentControllerProviderConfiguration(
            AuthBaseUrl,
            EntraTenantId,
            EntraClientId,
            entraClientSecret: string.Empty,
            EntraScope,
            InternalScope,
            ExchangePath);
    }

    internal static AgentControllerProviderConfiguration FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new AgentControllerProviderConfiguration(
            RequireHttpsEnvironment(environment, "AGENTCONTROLLER_AUTH_BASE_URL", nameof(AuthBaseUrl)),
            RequireEnvironment(environment, "AGENTCONTROLLER_ENTRA_TENANT_ID"),
            RequireEnvironment(environment, "AGENTCONTROLLER_ENTRA_CLIENT_ID"),
            RequireSecretEnvironment(environment, "AGENTCONTROLLER_ENTRA_CLIENT_SECRET"),
            RequireEnvironment(environment, "AGENTCONTROLLER_ENTRA_SCOPE"),
            AgentControllerTokenProvider.DefaultInternalScope,
            AgentControllerTokenProvider.DefaultExchangePath);
    }

    private static string RequireEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string variableName)
    {
        if (!environment.TryGetValue(variableName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"AgentController provider configuration is missing required environment variable {variableName}.");
        }
        return value.Trim();
    }

    private static string RequireSecretEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string variableName)
    {
        if (!environment.TryGetValue(variableName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"AgentController provider configuration is missing required environment variable {variableName}.");
        }
        return value;
    }

    private static string RequireHttpsEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string variableName,
        string fieldName)
    {
        var value = RequireEnvironment(environment, variableName);
        EnsureAbsoluteHttpsUrl(value, fieldName);
        return value;
    }

    private static void EnsureAbsoluteHttpsUrl(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"AgentController provider configuration {fieldName} must use an absolute HTTPS URL without user info, a query, or a fragment.");
        }
    }

    private static void EnsureGuid(string value, string fieldName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidOperationException($"AgentController provider configuration {fieldName} must be a GUID.");
        }
    }

    private static void EnsureDefaultScope(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsWhiteSpace)
            || !value.EndsWith("/.default", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"AgentController provider configuration {fieldName} must contain one /.default scope.");
        }
    }

    private static void RequireValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"AgentController provider configuration {fieldName} is required.");
        }
    }

    /// <summary>
    /// Returns the redacted configuration summary used for diagnostics. The
    /// client secret is never included.
    /// </summary>
    public override string ToString()
    {
        return $"AgentControllerProviderConfiguration(AuthBaseUrl={AuthBaseUrl},EntraTenantId={EntraTenantId},EntraClientId={EntraClientId},EntraScope={EntraScope},InternalScope={InternalScope},ExchangePath={ExchangePath})";
    }
}

/// <summary>
/// Successful AgentController operator-token exchange response. The
/// <see cref="AccessToken"/> is held in memory only by callers and never
/// persisted or logged by the provider.
/// </summary>
internal sealed record AgentControllerTokenExchange(string AccessToken, int ExpiresInSeconds, Uri OpsBaseUrl, string InternalScope);
