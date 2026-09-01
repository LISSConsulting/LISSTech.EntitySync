using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record ConnectionDefinitionRequest(
    string Vendor,
    string ConnectionId,
    string DisplayName,
    IReadOnlyDictionary<string, JsonElement> PublicConfiguration,
    IReadOnlyDictionary<string, string> SecretConfiguration);

public enum ConnectionDeleteOutcome
{
    Deleted,
    Disabled
}

public sealed record ConnectionDeleteResult(
    ConnectionDeleteOutcome Outcome,
    EntitySyncConnectionDefinition? Definition);

public sealed class ConnectionGenerationConflictException : InvalidOperationException
{
    public ConnectionGenerationConflictException(string connectionId, long expectedGeneration)
        : base(
            $"Connection '{connectionId}' is no longer at expected generation "
            + $"{expectedGeneration}.")
    {
    }
}

public sealed class ConnectionDefinitionService(
    IConnectionDefinitionRepository repository,
    IEntitySyncDataProtector protector,
    IConnectionRuntimeFactory runtimeFactory,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> KnownVendors = new(
        ["HaloPSA", "NetSuite", "NCentral", EntitySyncVendors.AgentController,
            EntitySyncVendors.BillCom, "OrchestraMSP"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<EntitySyncConnectionDefinition> CreateAsync(
        string tenantId,
        ConnectionDefinitionRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(actor);
        var validated = ValidateRequest(request);
        var now = timeProvider.GetUtcNow();
        var definition = new EntitySyncConnectionDefinition(
            tenantId,
            validated.ConnectionId,
            validated.Vendor,
            validated.DisplayName,
            1,
            true,
            SerializePublicConfiguration(validated.PublicConfiguration),
            ProtectSecrets(validated.SecretConfiguration),
            now,
            actor,
            now,
            actor);
        return await repository.InsertAsync(
                tenantId,
                definition,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EntitySyncConnectionDefinition> GetAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = RequireConnectionId(connectionId);
        return await repository.GetAsync(tenantId, connectionId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
    }

    public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
        string tenantId,
        string? vendor,
        bool? enabled,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        var normalizedVendor = vendor is null ? null : RequireKnownVendor(vendor);
        return repository.ListAsync(
            tenantId,
            normalizedVendor,
            enabled,
            cancellationToken);
    }

    public async Task<bool> TestAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        ValidateExpectedGeneration(expectedGeneration);
        try
        {
            await using var lease = await runtimeFactory.AcquireAsync(
                Require(tenantId, nameof(tenantId)),
                RequireConnectionId(connectionId),
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);
            return await lease.Adapter.TestConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The connection adapter is unavailable.", exception);
        }
    }

    public async Task<EntitySyncConnectionDefinition> UpdateAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        ConnectionDefinitionRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = RequireConnectionId(connectionId);
        ValidateExpectedGeneration(expectedGeneration);
        ArgumentNullException.ThrowIfNull(actor);
        var validated = ValidateRequest(request);
        if (!connectionId.Equals(validated.ConnectionId, StringComparison.Ordinal))
            throw new ArgumentException(
                "The request connection ID must match the route connection ID.",
                nameof(request));
        var current = await GetAsync(tenantId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (current.Generation != expectedGeneration)
            throw new ConnectionGenerationConflictException(connectionId, expectedGeneration);
        if (!current.Vendor.Equals(validated.Vendor, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "A connection vendor cannot change after creation.",
                nameof(request));
        var next = current.NextGeneration(
            validated.DisplayName,
            enabled: current.Enabled,
            SerializePublicConfiguration(validated.PublicConfiguration),
            ProtectSecrets(validated.SecretConfiguration),
            actor,
            timeProvider.GetUtcNow());
        return await repository.TryReplaceAsync(
                tenantId,
                connectionId,
                expectedGeneration,
                next,
                cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectionGenerationConflictException(
                connectionId,
                expectedGeneration);
    }

    public async Task<EntitySyncConnectionDefinition> DisableAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = RequireConnectionId(connectionId);
        ValidateExpectedGeneration(expectedGeneration);
        ArgumentNullException.ThrowIfNull(actor);
        var current = await GetAsync(tenantId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (current.Generation != expectedGeneration)
            throw new ConnectionGenerationConflictException(connectionId, expectedGeneration);
        var next = current.NextGeneration(
            current.DisplayName,
            enabled: false,
            current.PublicConfiguration,
            current.SecretCiphertext,
            actor,
            timeProvider.GetUtcNow());
        return await repository.TryReplaceAsync(
                tenantId,
                connectionId,
                expectedGeneration,
                next,
                cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectionGenerationConflictException(
                connectionId,
                expectedGeneration);
    }

    public async Task<ConnectionDeleteResult> DeleteAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = RequireConnectionId(connectionId);
        ValidateExpectedGeneration(expectedGeneration);
        ArgumentNullException.ThrowIfNull(actor);
        var result = await repository.TryDeleteAsync(
            tenantId,
            connectionId,
            expectedGeneration,
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConnectionDefinitionDeleteResult.Deleted =>
                new ConnectionDeleteResult(ConnectionDeleteOutcome.Deleted, null),
            ConnectionDefinitionDeleteResult.Referenced =>
                new ConnectionDeleteResult(
                    ConnectionDeleteOutcome.Disabled,
                    await DisableAsync(
                        tenantId,
                        connectionId,
                        expectedGeneration,
                        actor,
                        cancellationToken).ConfigureAwait(false)),
            ConnectionDefinitionDeleteResult.NotFound =>
                throw new ConnectionNotFoundException(tenantId, connectionId),
            ConnectionDefinitionDeleteResult.GenerationMismatch =>
                throw new ConnectionGenerationConflictException(
                    connectionId,
                    expectedGeneration),
            _ => throw new InvalidOperationException("Unknown connection delete result.")
        };
    }

    private string ProtectSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        var ordered = secrets
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var plaintext = JsonSerializer.Serialize(ordered);
        try
        {
            return protector.Protect(
                EntitySyncDataProtectionPurpose.ConnectionSecret,
                plaintext);
        }
        finally
        {
            ordered.Clear();
            plaintext = string.Empty;
        }
    }

    private static EntitySyncJsonValue SerializePublicConfiguration(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in values
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return new EntitySyncJsonValue(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static ConnectionDefinitionRequest ValidateRequest(
        ConnectionDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vendor = RequireKnownVendor(request.Vendor);
        var connectionId = RequireConnectionId(request.ConnectionId);
        var displayName = Require(request.DisplayName, nameof(request.DisplayName));
        ArgumentNullException.ThrowIfNull(request.PublicConfiguration);
        ArgumentNullException.ThrowIfNull(request.SecretConfiguration);
        var publicKeys = ValidateKeys(
            request.PublicConfiguration.Keys,
            nameof(request.PublicConfiguration));
        var secretKeys = ValidateKeys(
            request.SecretConfiguration.Keys,
            nameof(request.SecretConfiguration));
        if (publicKeys.Overlaps(secretKeys))
            throw new ArgumentException(
                "Public and secret configuration keys must not overlap.",
                nameof(request));
        foreach (var pair in request.PublicConfiguration)
        {
            if (pair.Value.ValueKind == JsonValueKind.Undefined)
                throw new ArgumentException(
                    $"Public configuration '{pair.Key}' is undefined.",
                    nameof(request));
        }
        foreach (var pair in request.SecretConfiguration)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                throw new ArgumentException(
                    $"Secret configuration '{pair.Key}' is required.",
                    nameof(request));
        }
        return new(
            vendor,
            connectionId,
            displayName,
            request.PublicConfiguration,
            request.SecretConfiguration);
    }

    private static HashSet<string> ValidateKeys(
        IEnumerable<string> keys,
        string parameterName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var normalized = Require(key, parameterName);
            if (!result.Add(normalized))
                throw new ArgumentException(
                    $"Configuration key '{normalized}' is duplicated.",
                    parameterName);
        }
        return result;
    }

    private static string RequireKnownVendor(string vendor)
    {
        var normalized = EntitySyncVendors.Normalize(Require(vendor, nameof(vendor)));
        if (!KnownVendors.Contains(normalized))
            throw new ArgumentException($"Vendor '{normalized}' is not supported.", nameof(vendor));
        return KnownVendors.Single(value => value.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequireConnectionId(string connectionId)
    {
        var value = Require(connectionId, nameof(connectionId));
        if (value.Length > 64
            || !char.IsLetterOrDigit(value[0])
            || value.Any(character =>
                !char.IsLetterOrDigit(character)
                && character is not '-' and not '_' and not '.'))
            throw new ArgumentException(
                "Connection ID must be 1-64 letters, numbers, dots, underscores, or "
                + "hyphens and must start with a letter or number.",
                nameof(connectionId));
        return value;
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();

    private static void ValidateExpectedGeneration(long expectedGeneration)
    {
        if (expectedGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedGeneration),
                expectedGeneration,
                "Expected generation must be positive.");
    }
}
