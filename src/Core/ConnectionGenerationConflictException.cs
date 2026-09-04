namespace LISSTech.EntitySync.Core;

// Domain-level connection-generation conflict. Runtime raises this without
// depending on Application; Application subclasses it for callers that prefer
// the LISSTech.EntitySync.Application namespace.
public class ConnectionGenerationConflictException : InvalidOperationException
{
    public ConnectionGenerationConflictException(string connectionId, long expectedGeneration)
        : base(
            $"Connection '{connectionId}' is no longer at expected generation "
            + $"{expectedGeneration}.")
    {
    }
}
