using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record ExpertSuiteQlResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);

public sealed class ExpertOperationService(
    IConnectionDefinitionRepository definitions,
    IConnectionRuntimeFactory runtimes)
{
    public async Task<ExpertSuiteQlResult> ExecuteSuiteQlAsync(
        string tenantId,
        string connectionId,
        string query,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (maximumRows is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows), maximumRows, "Maximum rows must be between 1 and 1000.");
        query = Require(query, nameof(query));
        if (query.Length > 20_000)
            throw new ArgumentException("SuiteQL cannot exceed 20000 characters.", nameof(query));
        if (!query.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only read-only SELECT SuiteQL is permitted.", nameof(query));
        var definition = await GetDefinitionAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        if (lease.Adapter is not ISuiteQlExpertAdapter expert)
            throw new NotSupportedException(
                "The selected connection does not support SuiteQL expert queries.");
        try
        {
            var rows = await expert.InvokeSuiteQlAsync(
                query, maximumRows + 1, cancellationToken).ConfigureAwait(false);
            return new ExpertSuiteQlResult(
                rows.Take(maximumRows).ToArray(),
                rows.Count > maximumRows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The SuiteQL dependency is unavailable.", exception);
        }
    }

    public async Task<EntityWriteResult> SetCustomPropertyAsync(
        string tenantId,
        string connectionId,
        string entityId,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        entityId = Require(entityId, nameof(entityId));
        name = Require(name, nameof(name));
        if (name.Length > 256)
            throw new ArgumentException("Property name cannot exceed 256 characters.", nameof(name));
        if (value?.Length > 4096)
            throw new ArgumentException("Property value cannot exceed 4096 characters.", nameof(value));
        var definition = await GetDefinitionAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        if (lease.Adapter is not ICustomPropertyExpertAdapter expert)
            throw new NotSupportedException(
                "The selected connection does not support expert custom-property writes.");
        try
        {
            return await expert.SetOrganizationCustomPropertyAsync(
                entityId, name, value ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The custom-property dependency is unavailable.", exception);
        }
    }

    public async Task<CustomPropertyReadResult> GetCustomPropertyAsync(
        string tenantId,
        string connectionId,
        string entityId,
        string name,
        CancellationToken cancellationToken)
    {
        entityId = Require(entityId, nameof(entityId));
        name = Require(name, nameof(name));
        if (name.Length > 256)
            throw new ArgumentException(
                "Property name cannot exceed 256 characters.", nameof(name));
        var definition = await GetDefinitionAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        await using var lease = await runtimes.AcquireAsync(
            tenantId, definition.ConnectionId, definition.Generation, cancellationToken)
            .ConfigureAwait(false);
        if (lease.Adapter is not ICustomPropertyExpertAdapter expert)
            throw new NotSupportedException(
                "The selected connection does not support expert custom-property readback.");
        try
        {
            return await expert.GetOrganizationCustomPropertyAsync(
                entityId, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EntitySyncDependencyUnavailableException(
                "The custom-property readback dependency is unavailable.", exception);
        }
    }

    private async Task<EntitySyncConnectionDefinition> GetDefinitionAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        connectionId = Require(connectionId, nameof(connectionId));
        var definition = await definitions.GetAsync(
            tenantId, connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
        if (!definition.Enabled) throw new ConnectionDisabledException(connectionId);
        return definition;
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}
