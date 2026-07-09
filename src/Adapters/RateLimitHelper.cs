namespace LISSTech.EntitySync.Adapters;

/// <summary>
/// Shared helpers for vendor adapters that throttle outbound HTTP traffic and
/// retry 429 TooManyRequests responses. Centralised so HaloPSA, N-central,
/// and NetSuite cannot drift out of sync on the retry policy.
/// </summary>
public static class RateLimitHelper
{
    // Honours the Retry-After response header (delta-seconds first, then
    // absolute date), falling through to an exponential backoff capped at 300
    // seconds when the server does not advertise a wait. Used by HaloPSA,
    // N-central, and NetSuite adapters via their respective SendWithRateLimitAsync
    // loops.
    public static TimeSpan RateLimitDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) return delay;
        }

        return TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, attempt)));
    }
}