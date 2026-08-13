namespace LISSTech.EntitySync.Adapters;

/// <summary>
/// Shared helpers for vendor adapters that throttle outbound HTTP traffic and
/// retry 429 TooManyRequests responses. Centralised so every adapter that
/// needs to honour 429 TooManyRequests cannot drift out of sync on the retry
/// policy; currently used by HaloPSA, N-central, and NetSuite. LTAC is
/// intentionally excluded because its customer-scope write endpoint does not
/// require outbound HTTP.
/// </summary>
public static class RateLimitHelper
{
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    // Header-derived and fallback delays share one hard ceiling. A vendor cannot
    // suspend a request beyond the caller's bounded retry budget.
    public static TimeSpan RateLimitDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan delay;
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            delay = delta;
        }
        else if (response.Headers.RetryAfter?.Date is DateTimeOffset date && date > DateTimeOffset.UtcNow)
        {
            delay = date - DateTimeOffset.UtcNow;
        }
        else
        {
            delay = TimeSpan.FromSeconds(15 * Math.Pow(2, attempt));
        }

        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }
}