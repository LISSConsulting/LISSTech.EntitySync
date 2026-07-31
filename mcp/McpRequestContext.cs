using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace LISSTech.EntitySync.Mcp;

public sealed class McpRequestContext
{
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

            var user = httpContextAccessor?.HttpContext?.User;
            var subject = user?.Identity?.IsAuthenticated == true ? user.FindFirstValue("sub") : null;
            if (string.IsNullOrWhiteSpace(subject))
                throw new InvalidOperationException("The authenticated OAuth access token is missing the required 'sub' claim.");

            return subject;
        }
    }

    public bool AllowProfiles { get; }
}
