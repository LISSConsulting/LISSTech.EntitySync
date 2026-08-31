using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Hosting;

public sealed class ConnectionRuntimeFactory(
    IConnectionDefinitionRepository connections,
    IEntitySyncDataProtector protector,
    IServerManagedEntityAdapterFactory adapterFactory) : IConnectionRuntimeFactory
{
    public async Task<IConnectionRuntimeLease> AcquireAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = Require(connectionId, nameof(connectionId));
        if (expectedGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedGeneration),
                expectedGeneration,
                "Expected generation must be positive.");
        var definition = await connections.GetAsync(
            tenantId,
            connectionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
        RequireAvailable(definition, expectedGeneration);

        var publicConfiguration = ReadPublicConfiguration(definition.PublicConfiguration);
        var plaintext = string.Empty;
        var secretConfiguration = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        IEntityAdapter? adapter = null;
        try
        {
            plaintext = protector.Unprotect(
                EntitySyncDataProtectionPurpose.ConnectionSecret,
                definition.SecretCiphertext);
            secretConfiguration = ReadSecretConfiguration(plaintext);
            adapter = await adapterFactory.CreateDurableAsync(
                definition.Vendor,
                publicConfiguration,
                secretConfiguration,
                cancellationToken).ConfigureAwait(false);
            if (!adapter.Vendor.Equals(definition.Vendor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Adapter vendor '{adapter.Vendor}' does not match connection vendor "
                    + $"'{definition.Vendor}'.");
            var current = await connections.GetAsync(
                tenantId,
                connectionId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectionNotFoundException(tenantId, connectionId);
            RequireAvailable(current, expectedGeneration);
            var lease = new ConnectionRuntimeLease(definition, adapter);
            adapter = null;
            return lease;
        }
        finally
        {
            publicConfiguration.Clear();
            secretConfiguration.Clear();
            plaintext = string.Empty;
            if (adapter is not null) await DisposeAdapterAsync(adapter).ConfigureAwait(false);
        }
    }

    public async Task<IConnectionRuntimeLease> AcquireCurrentAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        vendor = EntitySyncVendors.Normalize(Require(vendor, nameof(vendor)));
        EntitySyncConnectionDefinition definition;
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            definition = await connections.GetAsync(
                tenantId,
                connectionId.Trim(),
                cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectionNotFoundException(tenantId, connectionId.Trim());
            if (!definition.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                throw new ConnectionNotFoundException(tenantId, connectionId.Trim());
        }
        else
        {
            var matches = await connections.ListAsync(
                tenantId,
                vendor,
                enabled: true,
                cancellationToken).ConfigureAwait(false);
            definition = matches.Count switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException(
                    $"No enabled connection exists for vendor '{vendor}'."),
                _ => throw new InvalidOperationException(
                    $"Multiple enabled connections exist for vendor '{vendor}'. "
                    + "Specify a connection ID.")
            };
        }
        return await AcquireAsync(
            tenantId,
            definition.ConnectionId,
            definition.Generation,
            cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, JsonElement> ReadPublicConfiguration(
        EntitySyncJsonValue configuration)
    {
        using var document = JsonDocument.Parse(configuration.Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "Stored public connection configuration must be a JSON object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
                throw new InvalidOperationException(
                    $"Stored public connection configuration contains duplicate key "
                    + $"'{property.Name}'.");
        }
        return result;
    }

    private static Dictionary<string, string> ReadSecretConfiguration(string plaintext)
    {
        Dictionary<string, string>? deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(
                plaintext,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Stored secret connection configuration is invalid.",
                exception);
        }
        if (deserialized is null)
            throw new InvalidOperationException(
                "Stored secret connection configuration must be a JSON object.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in deserialized)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value)
                    || !result.TryAdd(pair.Key, pair.Value))
                    throw new InvalidOperationException(
                        "Stored secret connection configuration contains an invalid or duplicate key/value.");
            }
            return result;
        }
        catch
        {
            result.Clear();
            throw;
        }
        finally
        {
            deserialized.Clear();
        }
    }

    private static void RequireAvailable(
        EntitySyncConnectionDefinition definition,
        long expectedGeneration)
    {
        if (!definition.Enabled)
            throw new ConnectionDisabledException(definition.ConnectionId);
        if (definition.Generation != expectedGeneration)
            throw new StaleConnectionGenerationException(
                definition.ConnectionId,
                expectedGeneration,
                definition.Generation);
    }

    private static async ValueTask DisposeAdapterAsync(IEntityAdapter adapter)
    {
        if (adapter is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (adapter is IDisposable disposable)
            disposable.Dispose();
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();

    private sealed class ConnectionRuntimeLease(
        EntitySyncConnectionDefinition definition,
        IEntityAdapter adapter) : IConnectionRuntimeLease
    {
        private IEntityAdapter? adapter = adapter;

        public EntitySyncConnectionDefinition Definition { get; } = definition;
        public IEntityAdapter Adapter => adapter
            ?? throw new ObjectDisposedException(nameof(ConnectionRuntimeLease));

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref adapter, null);
            if (owned is not null) await DisposeAdapterAsync(owned).ConfigureAwait(false);
        }
    }
}
