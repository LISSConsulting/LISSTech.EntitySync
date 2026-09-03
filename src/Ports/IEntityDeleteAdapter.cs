using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityDeleteAdapter
{
    Task<EntityWriteResult> DeleteEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken);
}
