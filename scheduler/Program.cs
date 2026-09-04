using LISSTech.EntitySync.Application;
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

            var serviceVersion = typeof(EntitySyncControlWorker).Assembly
                .GetName().Version?.ToString(3)
                ?? throw new InvalidOperationException(
                    "EntitySync scheduler assembly version is unavailable.");
            var logfireSettings = LogfireLoggingSettings.FromCurrentEnvironment(
                builder.Environment.EnvironmentName,
                serviceVersion);
            LogfireLogging.Configure(builder.Services, builder.Logging, logfireSettings);

            var workerSettings = EntitySyncWorkerSettings.FromCurrentEnvironment();
            builder.Services.AddEntitySyncPlatform(
                Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty,
                EntitySyncHostMode.Scheduler,
                workerSettings);
            builder.Services.AddSingleton(
                EntitySyncControlOptions.FromEnvironment(workerSettings));
            builder.Services.AddSingleton<PostgresSyncWorkQueue>();
            builder.Services.AddSingleton<ICanonicalChangeRepository>(
                services => services.GetRequiredService<PostgresSyncWorkQueue>());
            builder.Services.AddSingleton<IEntitySyncWorkSignal>(
                services => services.GetRequiredService<PostgresSyncWorkQueue>());
            builder.Services.AddSingleton<PostgresRouteLock>();
            builder.Services.AddSingleton<IEntitySyncRouteLock>(
                services => services.GetRequiredService<PostgresRouteLock>());
            builder.Services.AddSingleton<IEntitySyncOperationRouteLock>(
                services => services.GetRequiredService<PostgresRouteLock>());
            builder.Services.AddSingleton<CanonicalChangeService>();
            builder.Services.AddSingleton<EntitySyncControlWorker>();
            builder.Services.AddHostedService(
                services => services.GetRequiredService<EntitySyncControlWorker>());
            builder.Services.AddSingleton(EntityRefreshOptions.FromEnvironment());
            builder.Services.AddSingleton<EntityRefreshWorker>();
            builder.Services.AddHostedService(
                services => services.GetRequiredService<EntityRefreshWorker>());
            builder.Services.AddSingleton<AuditRetentionWorker>(services =>
                new AuditRetentionWorker(
                    services.GetRequiredService<LISSTech.EntitySync.Ports.ISyncAuditRepository>(),
                    services.GetRequiredService<LISSTech.EntitySync.Ports.ISyncOperationRepository>(),
                    services.GetRequiredService<EntitySyncControlOptions>().TenantIds,
                    services.GetRequiredService<TimeProvider>(),
                    services.GetRequiredService<
                        Microsoft.Extensions.Logging.ILogger<AuditRetentionWorker>>()));
            builder.Services.AddHostedService(
                services => services.GetRequiredService<AuditRetentionWorker>());

            var app = builder.Build();
            app.Logger.LogInformation(
                "Logfire logging configured: {LogfireConfiguration}",
                logfireSettings);
            app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
            app.MapGet(
                "/status",
                (EntitySyncControlOptions options) => Results.Ok(new
                {
                    state = "running",
                    tenantCount = options.TenantIds.Count
                }));
            return app;
        }
    }
}
