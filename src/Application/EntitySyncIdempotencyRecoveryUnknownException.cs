namespace LISSTech.EntitySync.Application;

public sealed class EntitySyncIdempotencyRecoveryUnknownException(string message)
    : InvalidOperationException(message);
