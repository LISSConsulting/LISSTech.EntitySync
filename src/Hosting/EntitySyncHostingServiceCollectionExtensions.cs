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
        EntitySyncHostMode hostMode,
        EntitySyncWorkerSettings? workerSettings = null)
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
        if (hostMode == EntitySyncHostMode.LocalStdio)
        {
            services.AddSingleton(
                _ => InMemoryEntityConnectionRepository.CreateLocalProfile());
            services.AddSingleton<IEntityConnectionRepository>(
                provider => provider.GetRequiredService<InMemoryEntityConnectionRepository>());
            services.AddSingleton<IConnectionRuntimeFactory>(
                provider => provider.GetRequiredService<InMemoryEntityConnectionRepository>());
        }
        else
        {
            services.AddSingleton<IConnectionRuntimeFactory, ConnectionRuntimeFactory>();
        }
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
        if (workerSettings is not null)
        {
            services.AddSingleton(workerSettings);
            services.AddSingleton(
                new EntitySyncOperationWorkerOptions(workerSettings.LeaseDuration));
        }
        services.AddSingleton<EntitySyncPlanner>();
        services.AddSingleton<PlanManifestBuilder>();
        services.AddSingleton<DurablePlanService>();
        services.AddSingleton<SyncAuditService>();
        services.AddSingleton<SyncOperationService>();
        services.AddSingleton<VendorOutcomeReconciler>();
        services.AddSingleton<EntitySyncOperationWorker>();
        services.AddSingleton<SyncScheduleService>();
        services.AddSingleton<EntityExclusionService>();
        services.AddSingleton<ExpertOperationService>();
        services.AddScoped<ConnectionDefinitionService>();
        services.AddScoped<IEntitySyncControlCommands, EntitySyncControlCommands>();
        services.AddScoped<SyncPolicyService>();
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
            return ValidateDataProtectionKeyPath(
                Path.GetFullPath(configured.Trim()),
                allowCreate: hostMode == EntitySyncHostMode.LocalStdio);
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
        return ValidateDataProtectionKeyPath(
            Path.Combine(localData, "LISSTech", "EntitySync", "DataProtection-Keys"),
            allowCreate: true);
    }

    private static string ValidateDataProtectionKeyPath(
        string path,
        bool allowCreate)
    {
        if (!Directory.Exists(path))
        {
            if (!allowCreate)
                throw new InvalidOperationException(
                    $"Data-protection key directory '{path}' must already exist and be mounted.");
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            var required = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute;
            var forbidden = UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            if ((mode & required) != required || (mode & forbidden) != 0)
            {
                throw new InvalidOperationException(
                    $"Data-protection key directory '{path}' must be owner-only mode 0700.");
            }
        }

        var probePath = Path.Combine(
            path, $".entitysync-write-probe-{Guid.NewGuid():N}");
        try
        {
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Data-protection key directory '{path}' is not usable by this process.",
                exception);
        }
        finally
        {
            if (File.Exists(probePath)) File.Delete(probePath);
        }
        return path;
    }
}
