namespace LISSTech.EntitySync.Scheduler;

public sealed record EntitySyncSchedulerStatusSnapshot(
    string State,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? NextRunAt,
    string? PlanId,
    int Total,
    int Changed,
    int Unchanged,
    int PolicySkipped,
    int Succeeded,
    int Failed,
    int ApplySkipped,
    string? Error);

public sealed class EntitySyncSchedulerStatus
{
    private const int MaxErrorLength = 512;
    private EntitySyncSchedulerStatusSnapshot snapshot = new(
        "Waiting",
        null,
        null,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null);

    public EntitySyncSchedulerStatusSnapshot Snapshot => Volatile.Read(ref snapshot);

    internal void Publish(EntitySyncSchedulerStatusSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Interlocked.Exchange(ref snapshot, Bound(value));
    }

    internal void SetNextRunAt(DateTimeOffset nextRunAt)
    {
        while (true)
        {
            var observed = Snapshot;
            var replacement = observed with { NextRunAt = nextRunAt };
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, replacement, observed), observed))
                return;
        }
    }

    private static EntitySyncSchedulerStatusSnapshot Bound(EntitySyncSchedulerStatusSnapshot value) =>
        value.Error is { Length: > MaxErrorLength }
            ? value with { Error = value.Error[..MaxErrorLength] }
            : value;
}
