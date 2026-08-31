using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record SyncPolicyRequest(
    string Name,
    string RouteScope,
    EntitySyncPolicyDefinition Definition,
    bool Enabled);

public sealed class PolicyNotFoundException : KeyNotFoundException
{
    public PolicyNotFoundException(string tenantId, Guid policyId, int? version = null)
        : base(version.HasValue
            ? $"Policy '{policyId}' version {version} was not found for tenant '{tenantId}'."
            : $"Policy '{policyId}' was not found for tenant '{tenantId}'.")
    {
    }
}

public sealed class PolicyVersionConflictException : InvalidOperationException
{
    public PolicyVersionConflictException(Guid policyId, int expectedVersion)
        : base(
            $"Policy '{policyId}' is no longer at expected version {expectedVersion}.")
    {
    }
}

public sealed class SyncPolicyService(
    ISyncPolicyRepository policies,
    IConnectionDefinitionRepository connections,
    IConnectionRuntimeFactory runtimeFactory,
    TimeProvider timeProvider)
{
    private const string OrchestraVendor = "OrchestraMSP";

    public async Task<EntitySyncPolicy> CreateAsync(
        string tenantId,
        SyncPolicyRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);
        ArgumentNullException.ThrowIfNull(actor);
        var name = Require(request.Name, nameof(request.Name));
        var routeScope = Require(request.RouteScope, nameof(request.RouteScope));
        return await ValidateAndInsertAsync(
            tenantId,
            request.Definition,
            (source, target) => EntitySyncPolicy.Create(
                tenantId,
                Guid.NewGuid(),
                name,
                routeScope,
                request.Definition,
                request.Enabled,
                timeProvider.GetUtcNow(),
                actor),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntitySyncPolicy> CreateNextVersionAsync(
        string tenantId,
        Guid policyId,
        int expectedVersion,
        EntitySyncPolicyDefinition definition,
        bool? enabled,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        if (policyId == Guid.Empty)
            throw new ArgumentException("Policy ID cannot be empty.", nameof(policyId));
        if (expectedVersion <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion),
                expectedVersion,
                "Expected version must be positive.");
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(actor);
        var latest = await policies.GetLatestAsync(tenantId, policyId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new PolicyNotFoundException(tenantId, policyId);
        if (latest.Version != expectedVersion)
            throw new PolicyVersionConflictException(policyId, expectedVersion);
        return await ValidateAndInsertAsync(
            tenantId,
            definition,
            (source, target) => latest.NextVersion(
                actor,
                definition,
                timeProvider.GetUtcNow(),
                enabled),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntitySyncPolicy> GetVersionAsync(
        string tenantId,
        Guid policyId,
        int version,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        if (policyId == Guid.Empty)
            throw new ArgumentException("Policy ID cannot be empty.", nameof(policyId));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        return await policies.GetAsync(tenantId, policyId, version, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new PolicyNotFoundException(tenantId, policyId, version);
    }

    public Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(
        string tenantId,
        string? routeScope,
        bool? enabled,
        CancellationToken cancellationToken) =>
        policies.ListLatestAsync(
            Require(tenantId, nameof(tenantId)),
            routeScope is null ? null : Require(routeScope, nameof(routeScope)),
            enabled,
            cancellationToken);

    private async Task<EntitySyncPolicy> ValidateAndInsertAsync(
        string tenantId,
        EntitySyncPolicyDefinition definition,
        Func<EntitySyncConnectionDefinition, EntitySyncConnectionDefinition, EntitySyncPolicy> createPolicy,
        CancellationToken cancellationToken)
    {
        ValidateTopology(definition);
        var source = await RequireCurrentConnectionAsync(
            tenantId,
            definition.SourceConnectionId,
            definition.SourceVendor,
            cancellationToken).ConfigureAwait(false);
        var target = await RequireCurrentConnectionAsync(
            tenantId,
            definition.TargetConnectionId,
            definition.TargetVendor,
            cancellationToken).ConfigureAwait(false);

        await using var sourceLease = await runtimeFactory.AcquireAsync(
            tenantId,
            source.ConnectionId,
            source.Generation,
            cancellationToken).ConfigureAwait(false);
        await using var targetLease = await runtimeFactory.AcquireAsync(
            tenantId,
            target.ConnectionId,
            target.Generation,
            cancellationToken).ConfigureAwait(false);
        var sourceCapabilitiesTask = sourceLease.Adapter.GetCapabilitiesAsync(cancellationToken);
        var targetCapabilitiesTask = targetLease.Adapter.GetCapabilitiesAsync(cancellationToken);
        await Task.WhenAll(sourceCapabilitiesTask, targetCapabilitiesTask)
            .ConfigureAwait(false);
        ValidateCapabilities(
            definition,
            source,
            sourceCapabilitiesTask.Result,
            target,
            targetCapabilitiesTask.Result);

        await RequireUnchangedGenerationAsync(
            tenantId,
            source.ConnectionId,
            source.Generation,
            cancellationToken).ConfigureAwait(false);
        await RequireUnchangedGenerationAsync(
            tenantId,
            target.ConnectionId,
            target.Generation,
            cancellationToken).ConfigureAwait(false);

        var policy = createPolicy(source, target);
        if (await policies.TryInsertValidatedAsync(
                tenantId,
                policy,
                source.ConnectionId,
                source.Generation,
                target.ConnectionId,
                target.Generation,
                cancellationToken).ConfigureAwait(false))
            return policy;

        var currentSource = await connections.GetAsync(
            tenantId,
            source.ConnectionId,
            cancellationToken).ConfigureAwait(false);
        if (currentSource?.Generation != source.Generation || currentSource.Enabled != true)
            throw Stale(source, currentSource);
        var currentTarget = await connections.GetAsync(
            tenantId,
            target.ConnectionId,
            cancellationToken).ConfigureAwait(false);
        if (currentTarget?.Generation != target.Generation || currentTarget.Enabled != true)
            throw Stale(target, currentTarget);
        throw new PolicyVersionConflictException(policy.PolicyId, policy.Version - 1);
    }

    private async Task<EntitySyncConnectionDefinition> RequireCurrentConnectionAsync(
        string tenantId,
        string connectionId,
        string expectedVendor,
        CancellationToken cancellationToken)
    {
        var definition = await connections.GetAsync(
            tenantId,
            connectionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
        if (!definition.Enabled) throw new ConnectionDisabledException(connectionId);
        var normalizedVendor = EntitySyncVendors.Normalize(expectedVendor);
        if (!definition.Vendor.Equals(normalizedVendor, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Connection '{connectionId}' is vendor '{definition.Vendor}', not "
                + $"'{normalizedVendor}'.",
                nameof(expectedVendor));
        return definition;
    }

    private async Task RequireUnchangedGenerationAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var current = await connections.GetAsync(
            tenantId,
            connectionId,
            cancellationToken).ConfigureAwait(false);
        if (current?.Generation != expectedGeneration || current.Enabled != true)
        {
            throw new StaleConnectionGenerationException(
                connectionId,
                expectedGeneration,
                current?.Generation ?? 0);
        }
    }

    private static void ValidateTopology(EntitySyncPolicyDefinition definition)
    {
        var sourceIsOrchestra = definition.SourceVendor.Equals(
            OrchestraVendor,
            StringComparison.OrdinalIgnoreCase);
        var targetIsOrchestra = definition.TargetVendor.Equals(
            OrchestraVendor,
            StringComparison.OrdinalIgnoreCase);
        if (sourceIsOrchestra == targetIsOrchestra)
            throw new ArgumentException(
                "A production synchronization policy must have exactly one OrchestraMSP endpoint.",
                nameof(definition));
        if (definition.SourceConnectionId.Equals(
                definition.TargetConnectionId,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Source and target connection IDs must differ.",
                nameof(definition));
    }

    private static void ValidateCapabilities(
        EntitySyncPolicyDefinition definition,
        EntitySyncConnectionDefinition source,
        EntityAdapterCapabilities sourceCapabilities,
        EntitySyncConnectionDefinition target,
        EntityAdapterCapabilities targetCapabilities)
    {
        RequireCapabilityVendor(source, sourceCapabilities);
        RequireCapabilityVendor(target, targetCapabilities);
        var sourceEntity = RequireEntityType(
            sourceCapabilities,
            definition.SourceEntityType,
            "source");
        var targetEntity = RequireEntityType(
            targetCapabilities,
            definition.TargetEntityType,
            "target");
        RequireAction(sourceEntity, EntityAdapterActions.Read, "source");
        RequireAction(targetEntity, EntityAdapterActions.Update, "target");
        if (definition.CreateMissing)
            RequireAction(targetEntity, EntityAdapterActions.Create, "target");

        RequireField(
            sourceEntity,
            definition.SourceExternalIdName,
            "source external ID");
        RequireField(
            targetEntity,
            definition.TargetCustomFieldName,
            "target custom");
        foreach (var field in definition.AllowedFields)
            RequireField(targetEntity, field, "allowed");
        foreach (var field in definition.BlockedFields)
            RequireField(targetEntity, field, "blocked");
        if (!definition.ScheduledApplySafeSubset) return;
        foreach (var field in definition.AllowedFields)
        {
            if (!targetEntity.IsScheduledSafe(field))
                throw new ArgumentException(
                    $"Field '{field}' is not scheduled-safe for target entity type "
                    + $"'{targetEntity.EntityType}'.",
                    nameof(definition));
        }
        if (definition.CreateMissing
            && !targetEntity.SupportedActions.Contains(EntityAdapterActions.Create))
            throw new ArgumentException(
                "Scheduled create is not supported by the target connection.",
                nameof(definition));
    }

    private static void RequireCapabilityVendor(
        EntitySyncConnectionDefinition definition,
        EntityAdapterCapabilities capabilities)
    {
        if (!definition.Vendor.Equals(capabilities.Vendor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Adapter capability vendor '{capabilities.Vendor}' does not match connection "
                + $"vendor '{definition.Vendor}'.");
    }

    private static EntityTypeCapabilities RequireEntityType(
        EntityAdapterCapabilities capabilities,
        string entityType,
        string endpoint)
    {
        if (capabilities.TryGetEntityType(entityType, out var capability))
            return capability;
        throw new ArgumentException(
            $"Vendor '{capabilities.Vendor}' does not support {endpoint} entity type "
            + $"'{entityType}'.",
            nameof(entityType));
    }

    private static void RequireAction(
        EntityTypeCapabilities capabilities,
        string action,
        string endpoint)
    {
        if (!capabilities.SupportsAction(action))
            throw new ArgumentException(
                $"The {endpoint} entity type '{capabilities.EntityType}' does not support "
                + $"action '{action}'.",
                nameof(capabilities));
    }

    private static void RequireField(
        EntityTypeCapabilities capabilities,
        string? field,
        string role)
    {
        if (field is null) return;
        if (!capabilities.SupportsField(field))
            throw new ArgumentException(
                $"The {role} field '{field}' is not supported for entity type "
                + $"'{capabilities.EntityType}'.",
                nameof(field));
    }

    private static StaleConnectionGenerationException Stale(
        EntitySyncConnectionDefinition expected,
        EntitySyncConnectionDefinition? actual) =>
        new(expected.ConnectionId, expected.Generation, actual?.Generation ?? 0);

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}
