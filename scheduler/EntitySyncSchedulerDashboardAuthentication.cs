using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LISSTech.EntitySync.Scheduler;

internal sealed class EntitySyncSchedulerDashboardAuthentication
{
    internal const string PolicyName = "dashboard";
    internal const string TenantIdEnvironmentVariable = "DASHBOARD_ENTRA_TENANT_ID";
    internal const string ClientIdEnvironmentVariable = "DASHBOARD_ENTRA_CLIENT_ID";
    internal const string ClientSecretEnvironmentVariable = "DASHBOARD_ENTRA_CLIENT_SECRET";
    internal const string PublicOriginEnvironmentVariable = "DASHBOARD_PUBLIC_ORIGIN";
    internal const string CallbackPath = "/signin-oidc";

    private EntitySyncSchedulerDashboardAuthentication(
        string tenantId,
        string clientId,
        string clientSecret,
        Uri publicOrigin)
    {
        TenantId = tenantId;
        ClientId = clientId;
        ClientSecret = clientSecret;
        PublicOrigin = publicOrigin;
    }

    internal string TenantId { get; }
    internal string ClientId { get; }
    internal string ClientSecret { get; }
    internal Uri PublicOrigin { get; }

    internal static EntitySyncSchedulerDashboardAuthentication FromCurrentEnvironment()
    {
        var tenantId = RequireGuid(TenantIdEnvironmentVariable);
        var clientId = RequireGuid(ClientIdEnvironmentVariable);
        var clientSecret = RequireValue(ClientSecretEnvironmentVariable);
        var publicOriginValue = RequireValue(PublicOriginEnvironmentVariable);
        if (!Uri.TryCreate(publicOriginValue, UriKind.Absolute, out var publicOrigin)
            || !publicOrigin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(publicOrigin.UserInfo)
            || !string.IsNullOrEmpty(publicOrigin.Query)
            || !string.IsNullOrEmpty(publicOrigin.Fragment)
            || publicOrigin.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                $"{PublicOriginEnvironmentVariable} must be an HTTPS origin without a path, user info, query, or fragment.");
        }

        return new EntitySyncSchedulerDashboardAuthentication(
            tenantId,
            clientId,
            clientSecret,
            publicOrigin);
    }

    internal void Configure(MicrosoftIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Instance = "https://login.microsoftonline.com/";
        options.TenantId = TenantId;
        options.ClientId = ClientId;
        options.ClientSecret = ClientSecret;
        options.CallbackPath = CallbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.NameClaimType = "name";
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = new Uri(PublicOrigin, CallbackPath).AbsoluteUri;
            return Task.CompletedTask;
        };
    }

    internal static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Cookie.Name = "__Host-LISSTech.EntitySync.Dashboard";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    }

    private static string RequireGuid(string variableName)
    {
        var value = RequireValue(variableName);
        if (!Guid.TryParse(value, out _))
            throw new InvalidOperationException($"{variableName} must contain a GUID.");
        return value;
    }

    private static string RequireValue(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variableName} is required.");
        return value;
    }
}
