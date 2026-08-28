using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Hosting;

public interface IServerManagedEntityAdapterFactory
{
    Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken);

    string GetNetSuiteHaloChangeStateScope();
}
