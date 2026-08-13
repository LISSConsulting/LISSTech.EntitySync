using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace LISSTech.EntitySync.Mcp;

internal static class McpAuthorization
{
    internal static void AddPolicy(AuthorizationOptions options, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(requiredScope)) throw new ArgumentException("Required scope is required.", nameof(requiredScope));

        options.AddPolicy("mcp", policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => HasScope(context.User, requiredScope) && McpRequestContext.HasValidHttpIdentity(context.User)));
    }

    private static bool HasScope(ClaimsPrincipal user, string requiredScope) => user.Claims
        .Where(claim => claim.Type is "scope" or "scp")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(requiredScope, StringComparer.Ordinal);
}
