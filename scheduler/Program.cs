using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;

var app = EntitySyncSchedulerHost.Build(args);
await app.RunAsync();

namespace LISSTech.EntitySync.Scheduler
{
    internal static class EntitySyncSchedulerHost
    {
        internal static WebApplication Build(string[] args)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")))
            {
                Environment.SetEnvironmentVariable(
                    "OTEL_SERVICE_NAME",
                    "lisstech-entitysync-scheduler");
            }

            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });

            var serviceVersion = typeof(EntitySyncSchedulerWorker).Assembly.GetName().Version?.ToString(3)
                ?? throw new InvalidOperationException("EntitySync scheduler assembly version is unavailable.");
            var logfireSettings = LogfireLoggingSettings.FromCurrentEnvironment(
                builder.Environment.EnvironmentName,
                serviceVersion);
            LogfireLogging.Configure(builder.Services, builder.Logging, logfireSettings);
            var dashboardAuthentication =
                EntitySyncSchedulerDashboardAuthentication.FromCurrentEnvironment();
            builder.Services
                .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(dashboardAuthentication.Configure);
            builder.Services.Configure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                EntitySyncSchedulerDashboardAuthentication.ConfigureCookie);
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(
                    EntitySyncSchedulerDashboardAuthentication.PolicyName,
                    policy => policy.RequireAuthenticatedUser()));
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedProto
                    | ForwardedHeaders.XForwardedHost;
                options.ForwardLimit = 1;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });


            builder.Services.AddEntitySyncPlatform(
                Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty);
            builder.Services.AddSingleton<EntitySyncSchedulerOptions>();
            builder.Services.AddSingleton<EntitySyncSchedulerStatus>();
            builder.Services.AddSingleton<EntitySyncSchedulerDashboardStore>();
            builder.Services.AddSingleton(
                EntitySyncSchedulerRunAuthorization.FromCurrentEnvironment());
            builder.Services.AddSingleton<IEntitySyncSchedulerRunLock, PostgresEntitySyncSchedulerRunLock>();
            builder.Services.AddSingleton<IEntitySyncScheduledRun, EntitySyncScheduledRun>();
            builder.Services.AddSingleton<EntitySyncSchedulerWorker>();
            builder.Services.AddHostedService(
                services => services.GetRequiredService<EntitySyncSchedulerWorker>());

            var app = builder.Build();
            var schedulerOptions = app.Services.GetRequiredService<EntitySyncSchedulerOptions>();
            var adapterFactory = app.Services.GetRequiredService<IServerManagedEntityAdapterFactory>();
            if (schedulerOptions.AutomaticRunsEnabled)
            {
                adapterFactory.ValidateConfiguration(
                    schedulerOptions.Routes.SelectMany(route => new[] { route.SourceVendor, route.TargetVendor }));
                foreach (var route in schedulerOptions.Routes)
                {
                    _ = adapterFactory.GetChangeStateScope(
                        route.SourceVendor,
                        route.SourceConnectionId,
                        route.SourceEntityType,
                        route.TargetVendor,
                        route.TargetConnectionId,
                        route.TargetEntityType);
                }
            }
            app.Logger.LogInformation(
                "Logfire logging configured: {LogfireConfiguration}",
                logfireSettings);
            app.UseForwardedHeaders();
            app.UseAuthentication();
            app.UseAuthorization();
            app.RequireDashboardAuthenticationForAssets();
            app.MapDashboard();
            app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
                .AllowAnonymous();
            app.MapGet(
                    "/status",
                    (EntitySyncSchedulerStatus status) => Results.Ok(status.Snapshot))
                .RequireAuthorization(EntitySyncSchedulerDashboardAuthentication.PolicyName);
            app.MapPost(
                "/run",
                (HttpContext context,
                    EntitySyncSchedulerRunAuthorization authorization,
                    EntitySyncSchedulerWorker worker) =>
                {
                    if (!authorization.IsAuthorized(context.Request))
                    {
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return Results.Unauthorized();
                    }

                    return worker.TryRequestRun()
                        ? Results.Accepted("/status", new { accepted = true, status = "Queued" })
                        : Results.Conflict(new { accepted = false, status = "Busy" });
                });
            return app;
        }
    }
}
