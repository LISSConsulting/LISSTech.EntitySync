using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;

using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;

var transport = (Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio").Trim().ToLowerInvariant();

if (transport == "stdio")
{
    await RunStdioAsync(args);
    return;
}

if (transport == "http")
{
    await RunHttpAsync(args);
    return;
}

throw new InvalidOperationException("MCP_TRANSPORT must be 'stdio' or 'http'.");

static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    builder.Services.AddSingleton(new McpRequestContext("local", true));
    AddEntitySyncPlatform(builder.Services);

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    var authority = RequireHttpsUri("MCP_OAUTH_AUTHORITY");
    var resource = RequireHttpsUri("MCP_OAUTH_RESOURCE");
    var audience = (Environment.GetEnvironmentVariable("MCP_OAUTH_AUDIENCE") ?? resource).Trim();
    if (string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("MCP_OAUTH_AUDIENCE must contain the access-token audience when MCP_TRANSPORT=http.");

    var scopes = (Environment.GetEnvironmentVariable("MCP_OAUTH_SCOPES") ?? "mcp:tools")
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (scopes.Length == 0)
        throw new InvalidOperationException("MCP_OAUTH_SCOPES must contain at least one OAuth scope value.");

    var requiredScope = (Environment.GetEnvironmentVariable("MCP_OAUTH_REQUIRED_SCOPE") ?? "mcp:tools").Trim();
    if (string.IsNullOrWhiteSpace(requiredScope) || requiredScope.Any(char.IsWhiteSpace))
        throw new InvalidOperationException("MCP_OAUTH_REQUIRED_SCOPE must contain one access-token scope value.");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAudience = audience,
                NameClaimType = "name",
                RoleClaimType = "roles"
            };
        })
        .AddMcp(options =>
        {
            options.ResourceMetadata = new()
            {
                Resource = resource,
                ResourceName = "LISSTech EntitySync MCP Server",
                AuthorizationServers = { authority },
                ScopesSupported = scopes,
                BearerMethodsSupported = ["header"]
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("mcp", policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => HasScope(context.User, requiredScope)));
    });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<McpRequestContext>();

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();

    AddEntitySyncPlatform(builder.Services);

    var app = builder.Build();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapMcp("/mcp").RequireAuthorization("mcp");

    await app.RunAsync();
}

static void AddEntitySyncPlatform(IServiceCollection services)
{
    services.AddSingleton<IEntityConnectionRepository, InMemoryEntityConnectionRepository>();
    services.AddSingleton<IEntitySyncPlanRepository, InMemoryEntitySyncPlanRepository>();
    services.AddSingleton<IEntityMatcher, WeightedEntityMatcher>();
    services.AddSingleton<IEntityMapper, DefaultEntityMapper>();
    services.AddSingleton<EntitySyncPlanner>();
    services.AddSingleton<EntitySyncService>();
}

static string RequireHttpsUri(string variableName)
{
    var value = Environment.GetEnvironmentVariable(variableName)?.Trim();
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment))
        throw new InvalidOperationException($"{variableName} must be an absolute HTTPS URI without user info, a query, or a fragment when MCP_TRANSPORT=http.");

    return uri.AbsoluteUri;
}

static bool HasScope(System.Security.Claims.ClaimsPrincipal principal, string requiredScope)
{
    return principal.Claims
        .Where(claim => claim.Type is "scope" or "scp")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(requiredScope, StringComparer.Ordinal);
}
