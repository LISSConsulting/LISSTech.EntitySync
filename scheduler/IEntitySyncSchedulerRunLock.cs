namespace LISSTech.EntitySync.Scheduler;

public interface IEntitySyncSchedulerRunLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string routeKey, CancellationToken cancellationToken);
}
