using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace LISSTech.EntitySync.Mcp;

public sealed class McpRequestContext
{
    private const int MaximumIdentityClaimLength = 512;
    private readonly IHttpContextAccessor? httpContextAccessor;
    private readonly string? tenantId;

    public McpRequestContext(string tenantId, bool allowProfiles)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        this.tenantId = tenantId;
        AllowProfiles = allowProfiles;
    }

    public McpRequestContext(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        AllowProfiles = false;
    }

    public string TenantId
    {
        get
        {
            if (tenantId != null) return tenantId;
            var identity = GetHttpIdentity();
            return identity.Issuer + "::" + identity.Subject;
        }
    }

    public string Actor
    {
        get
        {
            if (tenantId != null) return tenantId;
            var identity = GetHttpIdentity();
            return identity.Issuer + "::" + identity.Subject;
        }
    }

    public bool AllowProfiles { get; }

    internal static bool HasValidHttpIdentity(ClaimsPrincipal? user)
    {
        try
        {
            _ = GetIdentity(user);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private OAuthIdentity GetHttpIdentity() => GetIdentity(httpContextAccessor?.HttpContext?.User);

    private static OAuthIdentity GetIdentity(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            throw new InvalidOperationException("An authenticated OAuth identity is required.");

        var subject = GetSingleClaim(user, "sub");
        var issuer = GetSingleClaim(user, "iss");
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || !issuerUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(issuerUri.UserInfo)
            || !string.IsNullOrEmpty(issuerUri.Query)
            || !string.IsNullOrEmpty(issuerUri.Fragment))
            throw new InvalidOperationException("The authenticated OAuth access token has an invalid 'iss' claim.");

        return new OAuthIdentity(issuerUri.AbsoluteUri.TrimEnd('/'), subject);
    }

    private static string GetSingleClaim(ClaimsPrincipal user, string type)
    {
        var values = user.FindAll(type).Select(claim => claim.Value).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new InvalidOperationException($"The authenticated OAuth access token must contain exactly one nonblank '{type}' claim.");

        var value = values[0].Trim();
        if (value.Length > MaximumIdentityClaimLength || value.Any(char.IsControl))
            throw new InvalidOperationException($"The authenticated OAuth access token has an invalid '{type}' claim.");
        return value;
    }

    private sealed record OAuthIdentity(string Issuer, string Subject);
}
