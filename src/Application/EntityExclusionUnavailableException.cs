namespace LISSTech.EntitySync.Application;

public sealed class EntityExclusionUnavailableException(string message, Exception innerException)
    : EntitySyncDependencyUnavailableException(message, innerException);
