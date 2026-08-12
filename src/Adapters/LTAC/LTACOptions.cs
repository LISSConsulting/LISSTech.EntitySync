namespace LISSTech.EntitySync.Adapters.LTAC;

public sealed class LTACOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;

    /// <summary>
    /// Asynchronous forced-refresh callback used after the AgentController
    /// authorization header is rejected with HTTP 401 or 403. The returned
    /// task must resolve to a freshly exchanged LTAC bearer token. Implementations
    /// MUST NOT accept credentials or endpoints from a remote caller; the
    /// server-managed configuration is the only allowed source.
    /// </summary>
    public Func<CancellationToken, Task<string>>? BearerTokenProvider { get; set; }

    /// <summary>
    /// Optional owner of the refresh callback's credentials and transport.
    /// The adapter disposes and releases it with the connection.
    /// </summary>
    public IDisposable? BearerTokenProviderOwner { get; set; }
}
