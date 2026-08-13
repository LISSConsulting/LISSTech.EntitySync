namespace LISSTech.EntitySync.Adapters;

/// <summary>
/// Applies the shared bounded 429 retry policy. Request spacing, response-size
/// limits, redirect refusal, and per-origin concurrency are enforced by the
/// hardened handler created by <see cref="VendorHttpClientFactory"/>.
/// </summary>
public sealed class RateLimitedHttpRequester : IDisposable
{

    /// <summary>Maximum number of retries permitted on HTTP 429 TooManyRequests before giving up.</summary>
    public const int MaxRateLimitRetries = 6;
    public static readonly TimeSpan MaximumTotalRetryDelay = TimeSpan.FromSeconds(90);

    private readonly string vendor;

    public RateLimitedHttpRequester(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) throw new ArgumentException("Vendor name is required.", nameof(vendor));
        this.vendor = vendor;
    }

    /// <summary>
    /// Sends <paramref name="createRequest"/> through <paramref name="httpClient"/> and
    /// recreates the request on every retry so per-attempt headers remain current.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> createRequest,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
        if (createRequest == null) throw new ArgumentNullException(nameof(createRequest));

        var totalRetryDelay = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitRetries) return response;

            var delay = RateLimitHelper.RateLimitDelay(response, attempt);
            if (totalRetryDelay + delay > MaximumTotalRetryDelay) return response;
            totalRetryDelay += delay;
            trace?.Invoke($"{vendor} rate limit reached. Waiting {(int)delay.TotalSeconds}s before retry {attempt + 1}/{MaxRateLimitRetries}.");
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }


    public void Dispose() { }
}