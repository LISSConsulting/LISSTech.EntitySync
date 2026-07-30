using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    AddEntitySyncPlatform(builder.Services, new McpRequestContext("local", true));

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    var apiKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
        throw new InvalidOperationException("MCP_API_KEY must contain at least 32 characters when MCP_TRANSPORT=http.");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();

    var tenantId = Environment.GetEnvironmentVariable("MCP_TENANT_ID")?.Trim();
    AddEntitySyncPlatform(builder.Services, new McpRequestContext(string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId, false));

    var app = builder.Build();

    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/mcp"),
        branch => branch.Use(async (context, next) =>
        {
            if (!HasValidBearerToken(context.Request, apiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                return;
            }

            await next(context);
        }));

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapMcp("/mcp");

    await app.RunAsync();
}

static void AddEntitySyncPlatform(IServiceCollection services, McpRequestContext context)
{
    services.AddSingleton(context);
    services.AddSingleton<IEntityConnectionRepository, InMemoryEntityConnectionRepository>();
    services.AddSingleton<IEntitySyncPlanRepository, InMemoryEntitySyncPlanRepository>();
    services.AddSingleton<IEntityMatcher, WeightedEntityMatcher>();
    services.AddSingleton<IEntityMapper, DefaultEntityMapper>();
    services.AddSingleton<EntitySyncPlanner>();
    services.AddSingleton<EntitySyncService>();
}

static bool HasValidBearerToken(HttpRequest request, string expectedToken)
{
    const string bearerPrefix = "Bearer ";
    var authorization = request.Headers["Authorization"].ToString();
    if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)) return false;

    var suppliedToken = authorization[bearerPrefix.Length..].Trim();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(suppliedToken),
        Encoding.UTF8.GetBytes(expectedToken));
}
