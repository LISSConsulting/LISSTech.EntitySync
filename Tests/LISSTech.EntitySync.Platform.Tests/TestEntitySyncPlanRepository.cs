using System.Collections.Concurrent;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

internal sealed class TestEntitySyncPlanRepository : IEntitySyncPlanRepository
{
    private readonly object capacityGate = new();
    private readonly ConcurrentDictionary<string, PlanEntry> plans = new(StringComparer.OrdinalIgnoreCase);

    public void Add(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.TenantId))
            throw new InvalidOperationException("Plan tenant is required.");
        var tenantId = plan.TenantId.Trim();
        var snapshot = Clone(plan);
        snapshot.TenantId = tenantId;
        lock (capacityGate)
        {
            if (!plans.TryAdd(Key(tenantId, plan.Id), new PlanEntry(snapshot)))
                throw new InvalidOperationException($"Plan '{plan.Id}' already exists.");
        }
    }

    public EntitySyncPlan Get(string tenantId, string planId)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate) return Clone(entry.Plan);
    }

    public void RecordInspection(
        string tenantId,
        string planId,
        string digest,
        int startIndex,
        int count)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            if (!string.Equals(entry.InspectedDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                entry.InspectedDigest = digest;
                entry.InspectedItems.Clear();
            }
            for (var index = startIndex;
                 index < Math.Min((long)startIndex + count, entry.Plan.Items.Count);
                 index++)
                entry.InspectedItems.Add(index);
        }
    }

    public bool TryTransition(
        string tenantId,
        string planId,
        string expectedStatus,
        string newStatus)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            if (!entry.Plan.Status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                return false;
            entry.Plan.Status = newStatus;
            return true;
        }
    }

    public bool TryApprove(string tenantId, string planId, string digest)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            if (!entry.Plan.Status.Equals(EntitySyncPlanStatuses.Draft, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(entry.InspectedDigest, digest, StringComparison.OrdinalIgnoreCase)
                || entry.InspectedItems.Count != entry.Plan.Items.Count)
                return false;
            entry.Plan.ApprovedDigest = digest;
            entry.Plan.Status = EntitySyncPlanStatuses.Approved;
            return true;
        }
    }

    private PlanEntry GetEntry(string tenantId, string planId) =>
        plans.TryGetValue(Key(tenantId, planId), out var entry)
            ? entry
            : throw new KeyNotFoundException($"Plan '{planId}' was not found.");

    private static EntitySyncPlan Clone(EntitySyncPlan plan)
    {
        var clone = JsonSerializer.Deserialize<EntitySyncPlan>(JsonSerializer.Serialize(plan))
            ?? throw new InvalidOperationException("Plan could not be copied.");
        foreach (var entity in clone.TargetCandidates
                     .Concat(clone.Items.Select(item => item.Source))
                     .Concat(clone.Items.Where(item => item.Target != null)
                         .Select(item => item.Target!)))
        {
            entity.ExternalIds = new Dictionary<string, string>(
                entity.ExternalIds, StringComparer.OrdinalIgnoreCase);
            entity.CustomFields = new Dictionary<string, string?>(
                entity.CustomFields, StringComparer.OrdinalIgnoreCase);
        }
        return clone;
    }

    private static string Key(string tenantId, string planId) =>
        $"{tenantId.Trim()}\n{planId.Trim()}";

    private sealed class PlanEntry(EntitySyncPlan plan)
    {
        internal object Gate { get; } = new();
        internal EntitySyncPlan Plan { get; } = plan;
        internal string? InspectedDigest { get; set; }
        internal HashSet<int> InspectedItems { get; } = [];
    }
}
