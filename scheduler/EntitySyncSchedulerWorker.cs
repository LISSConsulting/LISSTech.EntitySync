using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public sealed class EntitySyncSchedulerWorker : BackgroundService
{
    private readonly object runStateLock = new();
    private readonly IEntitySyncScheduledRun run;
    private readonly EntitySyncSchedulerStatus status;
    private readonly TimeProvider timeProvider;
    private readonly EntitySyncSchedulerOptions options;
    private readonly ILogger<EntitySyncSchedulerWorker> logger;
    private TaskCompletionSource runRequested = CreateRunSignal();
    private bool runInProgress;
    private bool runQueued;

    public EntitySyncSchedulerWorker(
        IEntitySyncScheduledRun run,
        EntitySyncSchedulerStatus status,
        TimeProvider timeProvider,
        EntitySyncSchedulerOptions? options = null,
        ILogger<EntitySyncSchedulerWorker>? logger = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? new EntitySyncSchedulerOptions();
        this.logger = logger ?? NullLogger<EntitySyncSchedulerWorker>.Instance;
    }

    internal bool TryRequestRun()
    {
        lock (runStateLock)
        {
            if (runInProgress || runQueued)
                return false;

            runQueued = true;
            if (!runRequested.TrySetResult())
            {
                runQueued = false;
                throw new InvalidOperationException("The scheduler run signal was already completed.");
            }

            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.AutomaticRunsEnabled)
        {
            logger.LogInformation(
                "Automatic scheduled synchronization is disabled; authenticated manual runs remain available.");
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitAndBeginRequestedRunAsync(stoppingToken).ConfigureAwait(false);
                await ExecuteRunBoundaryAsync(stoppingToken).ConfigureAwait(false);
            }
            return;
        }

        BeginInitialRun();
        await ExecuteRunBoundaryAsync(stoppingToken).ConfigureAwait(false);

        var nextScheduledAt = timeProvider.GetUtcNow() + EntitySyncSchedulerOptions.Interval;
        status.SetNextRunAt(nextScheduledAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            var scheduled = await WaitAndBeginNextRunAsync(
                nextScheduledAt,
                stoppingToken).ConfigureAwait(false);
            await ExecuteRunBoundaryAsync(stoppingToken).ConfigureAwait(false);

            var completedAt = timeProvider.GetUtcNow();
            if (scheduled || completedAt >= nextScheduledAt)
                nextScheduledAt = completedAt + EntitySyncSchedulerOptions.Interval;
            status.SetNextRunAt(nextScheduledAt);
        }
    }

    private async Task WaitAndBeginRequestedRunAsync(CancellationToken stoppingToken)
    {
        Task requested;
        lock (runStateLock) requested = runRequested.Task;
        await requested.WaitAsync(stoppingToken).ConfigureAwait(false);

        lock (runStateLock)
        {
            if (!runQueued)
                throw new InvalidOperationException("The scheduler run signal completed without a queued run.");

            runQueued = false;
            runRequested = CreateRunSignal();
            runInProgress = true;
        }
    }

    private async Task<bool> WaitAndBeginNextRunAsync(
        DateTimeOffset nextScheduledAt,
        CancellationToken stoppingToken)
    {
        Task requested;
        lock (runStateLock) requested = runRequested.Task;

        var remaining = nextScheduledAt - timeProvider.GetUtcNow();
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(remaining, timeProvider, delayCancellation.Token);
        await Task.WhenAny(delay, requested).ConfigureAwait(false);
        stoppingToken.ThrowIfCancellationRequested();

        var scheduled = delay.IsCompletedSuccessfully;
        if (!scheduled)
        {
            await delayCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await delay.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }

        lock (runStateLock)
        {
            if (!scheduled && !runQueued)
                throw new InvalidOperationException("The scheduler run signal completed without a queued run.");

            runQueued = false;
            runRequested = CreateRunSignal();
            runInProgress = true;
        }

        return scheduled;
    }

    private void BeginInitialRun()
    {
        lock (runStateLock)
        {
            if (runInProgress || runQueued)
                throw new InvalidOperationException("The scheduler cannot start with an active run request.");
            runInProgress = true;
        }
    }

    private async Task ExecuteRunBoundaryAsync(CancellationToken stoppingToken)
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
            lock (runStateLock) runInProgress = false;
        }
    }

    private static TaskCompletionSource CreateRunSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
