using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Hosting;

public interface IServerManagedEntityAdapterFactory
{
    Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken);

    void ValidateConfiguration(IEnumerable<string> vendors);

    string GetChangeStateScope(
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType);
}
