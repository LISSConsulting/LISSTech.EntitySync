using System.Text.Json;

using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Hosting;
public sealed record ServerManagedConnectionConfiguration(
    IReadOnlyDictionary<string, JsonElement> PublicConfiguration,
    IReadOnlyDictionary<string, string> SecretConfiguration,
    Guid? PlatformInstanceId = null);


public interface IServerManagedEntityAdapterFactory
{
    Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken);

    Task<IEntityAdapter> CreateDurableAsync(
        string vendor,
        IReadOnlyDictionary<string, JsonElement> publicConfiguration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        CancellationToken cancellationToken);

    Task<IEntityAdapter> CreateDurableAsync(
        string vendor,
        IReadOnlyDictionary<string, JsonElement> publicConfiguration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        long connectionGeneration,
        CancellationToken cancellationToken) =>
        CreateDurableAsync(
            vendor, publicConfiguration, secretConfiguration, cancellationToken);

    ServerManagedConnectionConfiguration GetConnectionConfiguration(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings);

    void ValidateNetSuiteHaloFixedRouteConfiguration();

    string GetChangeStateScope(
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType);
}
