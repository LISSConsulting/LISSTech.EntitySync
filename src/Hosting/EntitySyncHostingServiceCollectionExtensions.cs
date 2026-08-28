using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LISSTech.EntitySync.Hosting;

public static class EntitySyncHostingServiceCollectionExtensions
{
    public static IServiceCollection AddEntitySyncPlatform(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DATABASE_URL is required. EntitySync refuses to run without durable exclusion storage.");
        }

        services.AddSingleton(NpgsqlDataSource.Create(connectionString.Trim()));
        services.AddSingleton<IEntityConnectionRepository, InMemoryEntityConnectionRepository>();
        services.AddSingleton<IEntitySyncPlanRepository, InMemoryEntitySyncPlanRepository>();
        services.AddSingleton<IEntityExclusionRepository, PostgresEntityExclusionRepository>();
        services.AddSingleton<IEntitySyncChangeStateRepository, PostgresEntitySyncChangeStateRepository>();
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
}
