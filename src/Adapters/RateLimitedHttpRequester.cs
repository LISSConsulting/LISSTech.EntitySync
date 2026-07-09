namespace LISSTech.EntitySync.Adapters;

/// <summary>
/// Throttles outbound HTTP requests to honour Retry-After responses and a
/// minimum inter-request interval. Shared across HaloPSA, N-central, and
/// NetSuite adapters so they cannot drift out of sync on the retry policy or
/// throttle behaviour. The Retry-After parsing itself lives in
/// <see cref="RateLimitHelper.RateLimitDelay"/>.
/// </summary>
public sealed class RateLimitedHttpRequester : IDisposable
{
    /// <summary>Minimum wall-clock spacing between two outbound HTTP requests on the same adapter.</summary>
    public const int MinimumRequestIntervalMs = 500;

    /// <summary>Maximum number of retries permitted on HTTP 429 TooManyRequests before giving up.</summary>
    public const int MaxRateLimitRetries = 6;

    private readonly string vendor;
    private readonly SemaphoreSlim requestThrottle = new(1, 1);
    private DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;

    public RateLimitedHttpRequester(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) throw new ArgumentException("Vendor name is required.", nameof(vendor));
        this.vendor = vendor;
    }

    /// <summary>
    /// Sends <paramref name="createRequest"/> through <paramref name="httpClient"/> honouring
    /// the 429 Retry-After policy and the minimum-interval throttle. The factory closure
    /// re-runs on every retry so per-attempt headers (for example a freshly signed OAuth
    /// signature) are not cached.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> createRequest,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
        if (createRequest == null) throw new ArgumentNullException(nameof(createRequest));

        for (var attempt = 0; ; attempt++)
        {
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using var request = createRequest();
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitRetries) return response;

            var delay = RateLimitHelper.RateLimitDelay(response, attempt);
            trace?.Invoke($"{vendor} rate limit reached. Waiting {(int)delay.TotalSeconds}s before retry {attempt + 1}/{MaxRateLimitRetries}.");
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

    public void Dispose() => requestThrottle.Dispose();
}