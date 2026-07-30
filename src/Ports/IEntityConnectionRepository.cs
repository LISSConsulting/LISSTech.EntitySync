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

public interface IEntityConnectionRepository
{
    EntityConnectionRegistration Register(string tenantId, string? connectionId, IEntityAdapter adapter);
    EntityConnectionRegistration Resolve(string tenantId, string vendor, string? connectionId = null);
    IEntityConnectionLease Acquire(string tenantId, string vendor, string? connectionId = null, long? generation = null);
    IReadOnlyList<EntityConnectionRegistration> List(string tenantId);
}
