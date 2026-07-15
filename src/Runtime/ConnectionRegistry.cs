using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Runtime;

public static class ConnectionRegistry
{
    private static readonly Dictionary<string, IEntityAdapter> Adapters = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(IEntityAdapter adapter)
    {
        Adapters[EntitySyncVendors.Normalize(adapter.Vendor)] = adapter;
    }

    public static IEntityAdapter Get(string vendor)
    {
        if (Adapters.TryGetValue(EntitySyncVendors.Normalize(vendor), out var adapter)) return adapter;
        throw new InvalidOperationException($"No EntitySync connection exists for vendor '{vendor}'. Run Connect-EntitySyncVendor first.");
    }

    public static IReadOnlyList<EntitySyncConnection> Connections()
    {
        return Adapters
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new EntitySyncConnection { Vendor = pair.Key, Adapter = pair.Value })
            .ToArray();
    }
}
