namespace LISSTech.EntitySync.Application;

public sealed record EntitySyncApplyProgress(
    int Total,
    int Processed,
    int Succeeded,
    int Failed,
    int Skipped,
    EntitySyncApplyItemResult Item);
