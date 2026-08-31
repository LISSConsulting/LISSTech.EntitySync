using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LISSTech.EntitySync.Hosting;

public enum EntitySyncHostMode
{
    LocalStdio,
    Http,
    Scheduler
}

public static class EntitySyncHostingServiceCollectionExtensions
{
    private const string DataProtectionKeyPathEnvironmentVariable =
        "ENTITYSYNC_DATA_PROTECTION_KEY_PATH";

    public static IServiceCollection AddEntitySyncPlatform(
        this IServiceCollection services,
        string connectionString,
        EntitySyncHostMode hostMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DATABASE_URL is required. EntitySync refuses to run without durable exclusion storage.");
        }

        var keyPath = ResolveDataProtectionKeyPath(hostMode);
        services.AddDataProtection()
            .SetApplicationName("LISSTech.EntitySync.Control")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));

        services.AddSingleton(NpgsqlDataSource.Create(connectionString.Trim()));
        services.AddSingleton<IEntityConnectionRepository, InMemoryEntityConnectionRepository>();
        services.AddSingleton<IEntitySyncPlanRepository, InMemoryEntitySyncPlanRepository>();
        services.AddSingleton<IEntityExclusionRepository, PostgresEntityExclusionRepository>();
        services.AddSingleton<IEntitySyncChangeStateRepository, PostgresEntitySyncChangeStateRepository>();
        services.AddSingleton<IConnectionDefinitionRepository, PostgresConnectionDefinitionRepository>();
        services.AddSingleton<ISyncPolicyRepository, PostgresSyncPolicyRepository>();
        services.AddSingleton<IDurableSyncPlanRepository, PostgresDurableSyncPlanRepository>();
        services.AddSingleton<ISyncOperationRepository, PostgresSyncOperationRepository>();
        services.AddSingleton<ISyncScheduleRepository, PostgresSyncScheduleRepository>();
        services.AddSingleton<ISyncAuditRepository, PostgresSyncAuditRepository>();
        services.AddSingleton<PostgresIdempotencyRepository>();
        services.AddSingleton<IIdempotencyRepository>(
            provider => provider.GetRequiredService<PostgresIdempotencyRepository>());
        services.AddSingleton<IIdempotentCommandExecutor>(
            provider => provider.GetRequiredService<PostgresIdempotencyRepository>());
        services.AddSingleton<IEntitySyncDataProtector, EntitySyncDataProtector>();
        services.AddSingleton<IEntityMatcher, WeightedEntityMatcher>();
        services.AddSingleton<IEntityMapper, DefaultEntityMapper>();
        services.AddSingleton<IServerManagedEntityAdapterFactory, ServerManagedEntityAdapterFactory>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EntitySyncPlanner>();
        services.AddSingleton<EntitySyncService>();
        services.AddSingleton<EntityExclusionService>();
        services.AddHostedService<EntitySyncDatabaseMigrationHostedService>();
        return services;
    }

    private static string ResolveDataProtectionKeyPath(EntitySyncHostMode hostMode)
    {
        if (!Enum.IsDefined(hostMode))
            throw new ArgumentOutOfRangeException(nameof(hostMode), hostMode, "Unknown EntitySync host mode.");
        var configured = Environment.GetEnvironmentVariable(
            DataProtectionKeyPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());
        if (hostMode != EntitySyncHostMode.LocalStdio)
        {
            throw new InvalidOperationException(
                $"{DataProtectionKeyPathEnvironmentVariable} is required for {hostMode} hosts.");
        }

        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new InvalidOperationException(
                "A user-local application data directory is required for local stdio data protection.");
        return Path.Combine(localData, "LISSTech", "EntitySync", "DataProtection-Keys");
    }
}
