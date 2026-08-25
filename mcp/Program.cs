using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;
using Npgsql;

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
    var oauthChallengeHints = OAuthChallengeHints.Create(
        resource,
        Environment.GetEnvironmentVariable("MCP_OAUTH_AUTHORIZATION_ENDPOINT"),
        Environment.GetEnvironmentVariable("MCP_OAUTH_TOKEN_ENDPOINT"),
        Environment.GetEnvironmentVariable("MCP_OAUTH_PUBLIC_CLIENT_ID"),
        scopes);


    var requiredScope = (Environment.GetEnvironmentVariable("MCP_OAUTH_REQUIRED_SCOPE") ?? "mcp:tools").Trim();
    if (string.IsNullOrWhiteSpace(requiredScope) || requiredScope.Any(char.IsWhiteSpace))
        throw new InvalidOperationException("MCP_OAUTH_REQUIRED_SCOPE must contain one access-token scope value.");

    var builder = WebApplication.CreateBuilder(args);
    var serviceVersion = typeof(McpRequestContext).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("EntitySync MCP assembly version is unavailable.");
    var logfireSettings = LogfireLoggingSettings.FromCurrentEnvironment(
        builder.Environment.EnvironmentName,
        serviceVersion);
    LogfireLogging.Configure(builder.Services, builder.Logging, logfireSettings);

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

    builder.Services.AddAuthorization(options => McpAuthorization.AddPolicy(options, requiredScope));
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<McpRequestContext>();

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();

    AddEntitySyncPlatform(builder.Services);

    var app = builder.Build();
    app.Logger.LogInformation("Logfire logging configured: {LogfireConfiguration}", logfireSettings);
    await EntitySyncDatabaseMigrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
    if (oauthChallengeHints is not null)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
                    && context.Request.Path.StartsWithSegments("/mcp"))
                {
                    var challenges = context.Response.Headers["WWW-Authenticate"]
                        .Select(value => value ?? string.Empty);
                    context.Response.Headers["WWW-Authenticate"] = oauthChallengeHints.Append(challenges);
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
    }


    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapMcp("/mcp").RequireAuthorization("mcp");

    await app.RunAsync();
}

static void AddEntitySyncPlatform(IServiceCollection services)
{
    var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")?.Trim();
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("DATABASE_URL is required. EntitySync refuses to run without durable exclusion storage.");

    var dataSource = NpgsqlDataSource.Create(connectionString);
    services.AddSingleton(dataSource);
    services.AddSingleton<IEntityConnectionRepository, InMemoryEntityConnectionRepository>();
    services.AddSingleton<IEntitySyncPlanRepository, InMemoryEntitySyncPlanRepository>();
    services.AddSingleton<IEntityExclusionRepository, PostgresEntityExclusionRepository>();
    services.AddSingleton<IEntityMatcher, WeightedEntityMatcher>();
    services.AddSingleton<IEntityMapper, DefaultEntityMapper>();
    services.AddSingleton<EntitySyncPlanner>();
    services.AddSingleton<EntitySyncService>();
    services.AddSingleton<EntitySyncApplyCoordinator>();
    services.AddSingleton<EntityExclusionService>();
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


internal sealed class OAuthChallengeHints
{
    private OAuthChallengeHints(
        string resourceMetadataEndpoint,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? clientId,
        string? scopes)
    {
        ResourceMetadataEndpoint = resourceMetadataEndpoint;
        AuthorizationEndpoint = authorizationEndpoint;
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        Scopes = scopes;
    }

    private string ResourceMetadataEndpoint { get; }

    private string? AuthorizationEndpoint { get; }

    private string? TokenEndpoint { get; }

    private string? ClientId { get; }

    private string? Scopes { get; }

    internal static OAuthChallengeHints Create(
        string resource,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? clientId,
        IEnumerable<string> scopes)
    {
        var resourceMetadataEndpoint = BuildResourceMetadataEndpoint(resource);
        var values = new[] { authorizationEndpoint, tokenEndpoint, clientId };
        if (values.All(string.IsNullOrWhiteSpace))
            return new OAuthChallengeHints(resourceMetadataEndpoint, null, null, null, null);

        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "MCP_OAUTH_AUTHORIZATION_ENDPOINT, MCP_OAUTH_TOKEN_ENDPOINT, and MCP_OAUTH_PUBLIC_CLIENT_ID must be configured together.");

        var joinedScopes = string.Join(' ', scopes);
        return new OAuthChallengeHints(
            resourceMetadataEndpoint,
            ValidateHttpsEndpoint(authorizationEndpoint!, "MCP_OAUTH_AUTHORIZATION_ENDPOINT"),
            ValidateHttpsEndpoint(tokenEndpoint!, "MCP_OAUTH_TOKEN_ENDPOINT"),
            ValidateQuotedValue(clientId!, "MCP_OAUTH_PUBLIC_CLIENT_ID"),
            ValidateQuotedValue(joinedScopes, "MCP_OAUTH_SCOPES"));
    }

    internal string[] Append(IEnumerable<string> challenges)
    {
        return challenges
            .Select(challenge =>
            {
                if (!challenge.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    || !challenge.Contains("resource_metadata=", StringComparison.OrdinalIgnoreCase))
                    return challenge;

                var rewritten = ReplaceResourceMetadata(challenge);
                if (AuthorizationEndpoint is null
                    || rewritten.Contains("authorization_endpoint=", StringComparison.OrdinalIgnoreCase))
                    return rewritten;

                return $"{rewritten}, authorization_endpoint=\"{AuthorizationEndpoint}\", token_endpoint=\"{TokenEndpoint}\", client_id=\"{ClientId}\", scope=\"{Scopes}\"";
            })
            .ToArray();
    }

    private string ReplaceResourceMetadata(string challenge)
    {
        const string marker = "resource_metadata=\"";
        var valueStart = challenge.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (valueStart < 0) return challenge;
        valueStart += marker.Length;
        var valueEnd = challenge.IndexOf('"', valueStart);
        if (valueEnd < 0) return challenge;
        return challenge[..valueStart] + ResourceMetadataEndpoint + challenge[valueEnd..];
    }

    private static string BuildResourceMetadataEndpoint(string resource)
    {
        var uri = new Uri(ValidateHttpsEndpoint(resource, "MCP_OAUTH_RESOURCE"));
        if (!string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("MCP_OAUTH_RESOURCE must not contain a query.");

        return $"{uri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static string ValidateHttpsEndpoint(string value, string variableName)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"{variableName} must be an absolute HTTPS URI without user info or a fragment when configured.");

        return uri.AbsoluteUri;
    }

    private static string ValidateQuotedValue(string value, string variableName)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Any(character => character is '"' or '\\' or '\r' or '\n' || char.IsControl(character)))
            throw new InvalidOperationException(
                $"{variableName} contains characters that cannot be emitted safely in an OAuth challenge.");

        return trimmed;
    }
}