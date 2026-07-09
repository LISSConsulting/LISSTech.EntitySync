namespace LISSTech.EntitySync.Adapters;

/// <summary>
/// Cross-cutting helpers for vendor URLs. Centralised so adapters and the
/// vendor-connect command cannot drift out of sync on the trailing-slash rule
/// applied to <c>HttpClient.BaseAddress</c> values.
/// </summary>
public static class UrlHelpers
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it already ends in a forward
    /// slash, otherwise appends one. Used to normalise vendor base URLs before they
    /// are assigned to <c>HttpClient.BaseAddress</c> so that subsequent relative-URL
    /// requests (for example <c>api/client</c>) resolve correctly.
    /// </summary>
    public static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}
