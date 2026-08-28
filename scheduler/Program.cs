using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

            builder.Services.AddEntitySyncPlatform(
                Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty);
            builder.Services.AddSingleton<EntitySyncSchedulerOptions>();
            builder.Services.AddSingleton<EntitySyncSchedulerStatus>();
            builder.Services.AddSingleton<IEntitySyncSchedulerRunLock, PostgresEntitySyncSchedulerRunLock>();
            builder.Services.AddSingleton<IEntitySyncScheduledRun, EntitySyncScheduledRun>();
            builder.Services.AddSingleton<EntitySyncSchedulerWorker>();
            builder.Services.AddHostedService(
                services => services.GetRequiredService<EntitySyncSchedulerWorker>());

            var app = builder.Build();
            app.Services
                .GetRequiredService<IServerManagedEntityAdapterFactory>()
                .ValidateNetSuiteHaloFixedRouteConfiguration();
            app.Logger.LogInformation(
                "Logfire logging configured: {LogfireConfiguration}",
                logfireSettings);
            app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
            app.MapGet(
                "/status",
                (EntitySyncSchedulerStatus status) => Results.Ok(status.Snapshot));
            return app;
        }
    }
}
