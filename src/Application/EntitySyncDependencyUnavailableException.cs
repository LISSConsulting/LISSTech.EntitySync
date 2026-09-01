namespace LISSTech.EntitySync.Application;

public class EntitySyncDependencyUnavailableException(
    string message,
    Exception innerException)
    : InvalidOperationException(message, innerException);
