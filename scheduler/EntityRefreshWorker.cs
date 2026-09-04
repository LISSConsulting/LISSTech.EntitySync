using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public sealed class EntityRefreshOptions
{
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RefreshInterval { get; set; } = EntityRefreshConstants.DefaultRefreshInterval;
    public int DiscoveryBatch { get; set; } = 100;

    public static EntityRefreshOptions FromEnvironment()
    {
        var opts = new EntityRefreshOptions();
        var discovery = Environment.GetEnvironmentVariable("ENTITYSYNC_REFRESH_DISCOVERY_SECONDS");
        if (int.TryParse(discovery, out var discoverySeconds) && discoverySeconds > 0)
            opts.DiscoveryInterval = TimeSpan.FromSeconds(discoverySeconds);
        var scheduler = Environment.GetEnvironmentVariable("ENTITYSYNC_REFRESH_SCHEDULER_SECONDS");
        if (int.TryParse(scheduler, out var schedulerSeconds) && schedulerSeconds > 0)
            opts.SchedulerInterval = TimeSpan.FromSeconds(schedulerSeconds);
        var refresh = Environment.GetEnvironmentVariable("ENTITYSYNC_REFRESH_INTERVAL_MINUTES");
        if (int.TryParse(refresh, out var refreshMinutes) && refreshMinutes > 0)
            opts.RefreshInterval = TimeSpan.FromMinutes(refreshMinutes);
        return opts;
    }
}

public sealed class EntityRefreshWorker : BackgroundService
{
    private readonly IConnectionDefinitionRepository connections;
    private readonly EntityRefreshService refresh;
    private readonly IEntityRefreshCapabilityRepository capabilities;
    private readonly IEntityRefreshStateRepository states;
    private readonly EntitySyncControlOptions controlOptions;
    private readonly EntityRefreshOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntityRefreshWorker> logger;

    public EntityRefreshWorker(
        IConnectionDefinitionRepository connections,
        EntityRefreshService refresh,
        IEntityRefreshCapabilityRepository capabilities,
        IEntityRefreshStateRepository states,
        EntitySyncControlOptions controlOptions,
        EntityRefreshOptions options,
        TimeProvider timeProvider,
        ILogger<EntityRefreshWorker>? logger = null)
    {
        this.connections = connections;
        this.refresh = refresh;
        this.capabilities = capabilities;
        this.states = states;
        this.controlOptions = controlOptions;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<EntityRefreshWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            RunDiscoveryLoopAsync(stoppingToken),
            RunSchedulerLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RunDiscoveryLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "EntitySync refresh capability discovery failed.");
            }

            await DelaySafeAsync(options.DiscoveryInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunSchedulerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScheduleDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "EntitySync refresh scheduler tick failed.");
            }

            await DelaySafeAsync(options.SchedulerInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in controlOptions.TenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DiscoverTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DiscoverTenantAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EntitySyncConnectionDefinition> enabled;
        try
        {
            enabled = await connections.ListAsync(tenantId, vendor: null, enabled: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Capability discovery skipped for tenant {Tenant}: list failed.", tenantId);
            return;
        }

        foreach (var definition in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await refresh.DiscoverEntityTypesAsync(tenantId, definition, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Capability discovery failed for {Vendor}/{ConnectionId}; failure isolated.",
                    definition.Vendor, definition.ConnectionId);
            }
        }
    }

    private async Task ScheduleDueAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in controlOptions.TenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScheduleTenantDueAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScheduleTenantDueAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            await refresh.DiscoverAndQueueDueAsync(tenantId,
                timeProvider.GetUtcNow(), options.RefreshInterval, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Refresh scheduling failed for tenant {Tenant}; failures isolated.", tenantId);
        }
    }

    private static async Task DelaySafeAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
