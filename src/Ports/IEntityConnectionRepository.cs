using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public sealed record EntityConnectionRegistration(
    string Id,
    string TenantId,
    string Vendor,
    long Generation,
    IEntityAdapter Adapter);

public interface IEntityConnectionLease : IDisposable
{
    EntityConnectionRegistration Connection { get; }
}
public interface IEntityConnectionAdmission : IDisposable
{
    string TenantId { get; }
    string ConnectionId { get; }
}

public interface IConnectionRuntimeLease : IAsyncDisposable
{
    EntitySyncConnectionDefinition Definition { get; }
    IEntityAdapter Adapter { get; }
}

public interface IConnectionRuntimeFactory
{
    Task<IConnectionRuntimeLease> AcquireAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken);

    Task<IConnectionRuntimeLease> AcquireCurrentAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken);

    Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken);
}

public sealed class ConnectionNotFoundException : KeyNotFoundException
{
    public ConnectionNotFoundException(string tenantId, string connectionId)
        : base($"Connection '{connectionId}' was not found for tenant '{tenantId}'.")
    {
    }
}

public sealed class ConnectionDisabledException : InvalidOperationException
{
    public ConnectionDisabledException(string connectionId)
        : base($"Connection '{connectionId}' is disabled.")
    {
    }
}

public sealed class StaleConnectionGenerationException : InvalidOperationException
{
    public StaleConnectionGenerationException(
        string connectionId,
        long expectedGeneration,
        long actualGeneration)
        : base(
            $"Connection '{connectionId}' generation {expectedGeneration} is stale; "
            + $"current generation is {actualGeneration}.")
    {
        ConnectionId = connectionId;
        ExpectedGeneration = expectedGeneration;
        ActualGeneration = actualGeneration;
    }

    public string ConnectionId { get; }
    public long ExpectedGeneration { get; }
    public long ActualGeneration { get; }
}



public interface IEntityConnectionRepository
{
    IEntityConnectionAdmission BeginRegistration(string tenantId, string? connectionId, string vendor);
    EntityConnectionRegistration Register(string tenantId, string? connectionId, IEntityAdapter adapter);
    EntityConnectionRegistration Resolve(string tenantId, string vendor, string? connectionId = null);
    IEntityConnectionLease Acquire(string tenantId, string vendor, string? connectionId = null, long? generation = null);
    IReadOnlyList<EntityConnectionRegistration> List(string tenantId);
}
