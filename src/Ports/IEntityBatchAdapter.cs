using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityBatchAdapter
{
    Task<EntityWriteResult> ApplyBatchAsync(
        IReadOnlyList<EntityWriteRequest> requests,
        CancellationToken cancellationToken);
}
