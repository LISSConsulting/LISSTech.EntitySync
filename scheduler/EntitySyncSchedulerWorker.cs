using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public sealed class EntitySyncSchedulerWorker : BackgroundService
{
    private readonly IEntitySyncScheduledRun run;
    private readonly EntitySyncSchedulerStatus status;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntitySyncSchedulerWorker> logger;

    public EntitySyncSchedulerWorker(
        IEntitySyncScheduledRun run,
        EntitySyncSchedulerStatus status,
        TimeProvider timeProvider,
        ILogger<EntitySyncSchedulerWorker>? logger = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<EntitySyncSchedulerWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await run.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Scheduled synchronization escaped its run boundary. ExceptionType={ExceptionType}; Message={ErrorMessage}",
                    exception.GetType().Name,
                    "Scheduled synchronization failed.");
            }
            finally
            {
                var completedAt = timeProvider.GetUtcNow();
                status.SetNextRunAt(completedAt + EntitySyncSchedulerOptions.Interval);
            }

            await Task.Delay(
                EntitySyncSchedulerOptions.Interval,
                timeProvider,
                stoppingToken).ConfigureAwait(false);
        }
    }
}
