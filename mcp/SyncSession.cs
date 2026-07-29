using System.Collections.Concurrent;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Mcp;

public sealed class SyncSession
{
    private readonly ConcurrentDictionary<string, EntitySyncPlan> plans = new(StringComparer.OrdinalIgnoreCase);

    public void StorePlan(string id, EntitySyncPlan plan) => plans[id] = plan;

    public EntitySyncPlan? GetPlan(string id) => plans.TryGetValue(id, out var plan) ? plan : null;

    public IReadOnlyDictionary<string, EntitySyncPlan> Plans => plans;

    public IReadOnlyList<string> ConnectedVendors =>
        ConnectionRegistry.Connections().Select(c => c.Vendor).ToList();
}
