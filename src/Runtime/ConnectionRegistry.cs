using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Runtime;

public static class ConnectionRegistry
{
    private const string PowerShellTenant = "powershell";
    private static readonly InMemoryEntityConnectionRepository ConnectionsRepository =
        InMemoryEntityConnectionRepository.CreateLocalProfile();

    public static void Set(IEntityAdapter adapter)
    {
        var vendor = EntitySyncVendors.Normalize(adapter.Vendor);
        ConnectionsRepository.Register(PowerShellTenant, vendor.ToLowerInvariant(), adapter);
    }

    public static IEntityConnectionLease Acquire(string vendor)
    {
        return ConnectionsRepository.Acquire(PowerShellTenant, vendor);
    }

    public static IReadOnlyList<EntitySyncConnection> Connections()
    {
        return ConnectionsRepository.List(PowerShellTenant)
            .Select(connection => new EntitySyncConnection { Vendor = connection.Vendor, Adapter = connection.Adapter })
            .ToArray();
    }
}
