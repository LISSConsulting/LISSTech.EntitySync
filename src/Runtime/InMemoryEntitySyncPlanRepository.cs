using System.Collections.Concurrent;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntitySyncPlanRepository : IEntitySyncPlanRepository
{
    private const int MaxPlansPerTenant = 20;
    private readonly object capacityGate = new();
    private readonly ConcurrentDictionary<string, PlanEntry> plans = new(StringComparer.OrdinalIgnoreCase);

    public void Add(EntitySyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.TenantId)) throw new InvalidOperationException("Plan tenant is required.");
        var tenantId = plan.TenantId.Trim();
        var snapshot = Clone(plan);
        snapshot.TenantId = tenantId;
        lock (capacityGate)
        {
            RemoveExpiredPlans();
            if (plans.Values.Count(entry => entry.Plan.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase)) >= MaxPlansPerTenant)
                throw new InvalidOperationException($"Tenant plan limit of {MaxPlansPerTenant} has been reached. Wait for existing plans to expire.");
            if (!plans.TryAdd(Key(tenantId, plan.Id), new PlanEntry(snapshot))) throw new InvalidOperationException($"Plan '{plan.Id}' already exists.");
        }
    }

    public EntitySyncPlan Get(string tenantId, string planId)
    {
        var key = Key(tenantId, planId);
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            EnsureCurrent(key, entry, planId);
            return Clone(entry.Plan);
        }
    }

    public void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count)
    {
        if (startIndex < 0) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            EnsureCurrent(Key(tenantId, planId), entry, planId);
            if (!string.Equals(entry.InspectedDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                entry.InspectedDigest = digest;
                entry.InspectedItems.Clear();
            }
            for (var index = startIndex; index < Math.Min((long)startIndex + count, entry.Plan.Items.Count); index++) entry.InspectedItems.Add(index);
        }
    }

    public bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            if (!IsCurrent(Key(tenantId, planId), entry)) return false;
            var plan = entry.Plan;
            if (!plan.Status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase)) return false;
            plan.Status = newStatus;
            return true;
        }
    }

    public bool TryApprove(string tenantId, string planId, string digest)
    {
        var entry = GetEntry(tenantId, planId);
        lock (entry.Gate)
        {
            if (!IsCurrent(Key(tenantId, planId), entry)) return false;
            var plan = entry.Plan;
            if (!plan.Status.Equals(EntitySyncPlanStatuses.Draft, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(entry.InspectedDigest, digest, StringComparison.OrdinalIgnoreCase) || entry.InspectedItems.Count != plan.Items.Count) return false;
            plan.ApprovedDigest = digest;
            plan.Status = EntitySyncPlanStatuses.Approved;
            return true;
        }
    }

    private PlanEntry GetEntry(string tenantId, string planId)
    {
        var key = Key(tenantId, planId);
        if (!plans.TryGetValue(key, out var entry)) throw new KeyNotFoundException($"Plan '{planId}' was not found.");
        lock (entry.Gate)
        {
            EnsureCurrent(key, entry, planId);
            if (entry.Plan.ExpiresAt <= DateTimeOffset.UtcNow && !entry.Plan.Status.Equals(EntitySyncPlanStatuses.Applying, StringComparison.OrdinalIgnoreCase))
            {
                plans.TryRemove(key, out _);
                throw new InvalidOperationException($"Plan '{planId}' has expired.");
            }
        }
        return entry;
    }

    private void RemoveExpiredPlans()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in plans.ToArray())
        {
            lock (pair.Value.Gate)
            {
                if (IsCurrent(pair.Key, pair.Value)
                    && pair.Value.Plan.ExpiresAt <= now
                    && !pair.Value.Plan.Status.Equals(EntitySyncPlanStatuses.Applying, StringComparison.OrdinalIgnoreCase))
                    plans.TryRemove(pair.Key, out _);
            }
        }
    }

    private bool IsCurrent(string key, PlanEntry entry) => plans.TryGetValue(key, out var current) && ReferenceEquals(current, entry);

    private void EnsureCurrent(string key, PlanEntry entry, string planId)
    {
        if (!IsCurrent(key, entry)) throw new KeyNotFoundException($"Plan '{planId}' was not found.");
    }

    private static EntitySyncPlan Clone(EntitySyncPlan plan)
    {
        var clone = JsonSerializer.Deserialize<EntitySyncPlan>(JsonSerializer.Serialize(plan))
            ?? throw new InvalidOperationException("Plan could not be copied.");
        foreach (var entity in clone.TargetCandidates
            .Concat(clone.Items.Select(item => item.Source))
            .Concat(clone.Items.Where(item => item.Target != null).Select(item => item.Target!)))
        {
            entity.ExternalIds = new Dictionary<string, string>(entity.ExternalIds, StringComparer.OrdinalIgnoreCase);
            entity.CustomFields = new Dictionary<string, string?>(entity.CustomFields, StringComparer.OrdinalIgnoreCase);
        }
        return clone;
    }

    private sealed class PlanEntry(EntitySyncPlan plan)
    {
        public object Gate { get; } = new();
        public EntitySyncPlan Plan { get; } = plan;
        public string? InspectedDigest { get; set; }
        public HashSet<int> InspectedItems { get; } = [];
    }

    private static string Key(string tenantId, string planId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(planId)) throw new ArgumentException("Plan ID is required.", nameof(planId));
        return $"{tenantId.Trim()}\n{planId.Trim()}";
    }
}
