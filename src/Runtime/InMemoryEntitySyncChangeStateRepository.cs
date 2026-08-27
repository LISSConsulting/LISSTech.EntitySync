using System.Collections.Concurrent;
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
            if (states.TryGetValue(ChangeStateKey.Create(route, sourceEntityId), out var state))
                result[state.SourceEntityId] = Snapshot(state);
        }

        return Task.FromResult<IReadOnlyDictionary<string, EntitySyncChangeState>>(result);
    }

    public Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(state);
        states.AddOrUpdate(
            ChangeStateKey.Create(state.Route, state.SourceEntityId),
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
        public static ChangeStateKey Create(EntitySyncChangeStateRoute route, string sourceEntityId) =>
            new(
                route.TenantId,
                route.Scope,
                route.SourceVendor,
                route.SourceConnectionId,
                route.SourceEntityType,
                route.TargetVendor,
                route.TargetConnectionId,
                route.TargetEntityType,
                sourceEntityId.ToLowerInvariant());
    }
}
