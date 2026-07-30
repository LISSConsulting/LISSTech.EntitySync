using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntitySyncPlanRepository
{
    void Add(EntitySyncPlan plan);
    EntitySyncPlan Get(string tenantId, string planId);
    void RecordInspection(string tenantId, string planId, string digest, int startIndex, int count);
    bool TryApprove(string tenantId, string planId, string digest);
    bool TryTransition(string tenantId, string planId, string expectedStatus, string newStatus);
}
