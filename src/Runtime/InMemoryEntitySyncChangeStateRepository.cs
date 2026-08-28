using System.Collections.Concurrent;
using System.Text;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntitySyncChangeStateRepository : IEntitySyncChangeStateRepository
{
    private readonly ConcurrentDictionary<ChangeStateKey, EntitySyncChangeState> states = new();

    public Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
        EntitySyncChangeStateRoute route,
        IReadOnlyCollection<string> sourceEntityIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<string, EntitySyncChangeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceEntityId in sourceEntityIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceEntityKey = EntitySyncChangeStatePersistence.NormalizeSourceKey(route, sourceEntityId);
            if (states.TryGetValue(ChangeStateKey.Create(route, sourceEntityKey), out var state))
                result[state.SourceEntityId] = Snapshot(state);
        }

        return Task.FromResult<IReadOnlyDictionary<string, EntitySyncChangeState>>(result);
    }

    public Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validated = EntitySyncChangeStatePersistence.ValidateState(state);
        var snapshot = Snapshot(validated.State);
        states.AddOrUpdate(
            ChangeStateKey.Create(validated.State.Route, validated.SourceEntityKey),
            static (_, replacement) => replacement,
            static (_, _, replacement) => replacement,
            snapshot);
        return Task.CompletedTask;
    }

    private static EntitySyncChangeState Snapshot(EntitySyncChangeState state) =>
        state with { Route = state.Route with { } };

    private readonly record struct ChangeStateKey(
        string TenantId,
        string Scope,
        string SourceVendor,
        string SourceConnectionId,
        string SourceEntityType,
        string TargetVendor,
        string TargetConnectionId,
        string TargetEntityType,
        string SourceEntityKey)
    {
        public static ChangeStateKey Create(EntitySyncChangeStateRoute route, string sourceEntityKey) =>
            new(
                route.TenantId,
                route.Scope,
                route.SourceVendor,
                route.SourceConnectionId,
                route.SourceEntityType,
                route.TargetVendor,
                route.TargetConnectionId,
                route.TargetEntityType,
                sourceEntityKey);
    }
}

internal static class EntitySyncChangeStatePersistence
{
    private const int MaximumStateTextLength = 512;
    // PostgreSQL's default 8 KiB B-tree pages reject index tuples near 2704 bytes.
    // Keep the indexed text payload at 2000 bytes to leave conservative tuple-overhead headroom.
    private const int MaximumIndexedIdentityUtf8Bytes = 2000;

    public static ValidatedEntitySyncChangeState ValidateState(EntitySyncChangeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Route);
        var sourceEntityId = Require(state.SourceEntityId, nameof(state.SourceEntityId));
        var normalizedState = state with
        {
            SourceEntityId = sourceEntityId,
            SourceName = Require(state.SourceName, nameof(state.SourceName)),
            TargetEntityId = Require(state.TargetEntityId, nameof(state.TargetEntityId))
        };
        var sourceEntityKey = NormalizeValidatedSourceId(sourceEntityId);
        EnsureIndexedIdentityFits(normalizedState.Route, sourceEntityKey);
        return new ValidatedEntitySyncChangeState(normalizedState, sourceEntityKey);

    }

    public static string NormalizeSourceKey(EntitySyncChangeStateRoute route, string sourceEntityId)
    {
        ArgumentNullException.ThrowIfNull(route);
        var sourceEntityKey = NormalizeValidatedSourceId(Require(sourceEntityId, nameof(sourceEntityId)));
        EnsureIndexedIdentityFits(route, sourceEntityKey);
        return sourceEntityKey;
    }

    private static string NormalizeValidatedSourceId(string sourceEntityId) =>
        sourceEntityId.ToLowerInvariant();

    private static void EnsureIndexedIdentityFits(
        EntitySyncChangeStateRoute route,
        string sourceEntityKey)
    {
        var byteCount =
            (long)Encoding.UTF8.GetByteCount(route.TenantId) +
            Encoding.UTF8.GetByteCount(route.Scope) +
            Encoding.UTF8.GetByteCount(route.SourceVendor) +
            Encoding.UTF8.GetByteCount(route.SourceConnectionId) +
            Encoding.UTF8.GetByteCount(route.SourceEntityType) +
            Encoding.UTF8.GetByteCount(route.TargetVendor) +
            Encoding.UTF8.GetByteCount(route.TargetConnectionId) +
            Encoding.UTF8.GetByteCount(route.TargetEntityType) +
            Encoding.UTF8.GetByteCount(sourceEntityKey);
        if (byteCount > MaximumIndexedIdentityUtf8Bytes)
            throw new ArgumentException(
                $"Indexed change-state identity cannot exceed {MaximumIndexedIdentityUtf8Bytes} UTF-8 bytes.",
                nameof(route));
    }

    private static string Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumStateTextLength)
            throw new ArgumentException($"{name} cannot exceed {MaximumStateTextLength} characters.", name);
        return trimmed;
    }
}

internal readonly record struct ValidatedEntitySyncChangeState(
    EntitySyncChangeState State,
    string SourceEntityKey);
