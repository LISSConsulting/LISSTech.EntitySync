using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntitySyncChangeStateRepositoryTests
{
    private const string ScopeA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ScopeB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ChangeStateUpsertReplacesOnlyTheSameRouteAndSource()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        await repository.UpsertAsync(State(route, "42", "target-1", ScopeA), default);
        await repository.UpsertAsync(State(route, "42", "target-2", ScopeB), default);
        await repository.UpsertAsync(State(Route(ScopeB), "42", "target-3", ScopeA), default);

        var firstRoute = await repository.GetBySourceIdsAsync(route, ["42"], default);
        var secondRoute = await repository.GetBySourceIdsAsync(Route(ScopeB), ["42"], default);

        Assert.Equal("target-2", Assert.Single(firstRoute).Value.TargetEntityId);
        Assert.Equal("target-3", Assert.Single(secondRoute).Value.TargetEntityId);
    }

    [Fact]
    public async Task ChangeStateSourceIdentityIsCaseInsensitive()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        await repository.UpsertAsync(State(route, "Customer-42", "target-1", ScopeA), default);
        await repository.UpsertAsync(State(route, "CUSTOMER-42", "target-2", ScopeB), default);

        var result = await repository.GetBySourceIdsAsync(route, ["customer-42"], default);

        var state = Assert.Single(result).Value;
        Assert.Equal("CUSTOMER-42", state.SourceEntityId);
        Assert.Equal("target-2", state.TargetEntityId);
        Assert.True(result.ContainsKey("CUSTOMER-42"));
    }

    [Fact]
    public async Task ChangeStateBatchReadReturnsOnlyRequestedSources()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        await repository.UpsertAsync(State(route, "1", "target-1", ScopeA), default);
        await repository.UpsertAsync(State(route, "2", "target-2", ScopeB), default);
        await repository.UpsertAsync(State(route, "3", "target-3", ScopeA), default);

        var result = await repository.GetBySourceIdsAsync(route, ["1", "3", "missing"], default);

        Assert.Equal(2, result.Count);
        Assert.Equal("target-1", result["1"].TargetEntityId);
        Assert.Equal("target-3", result["3"].TargetEntityId);
        Assert.False(result.ContainsKey("2"));
    }

    [Fact]
    public async Task ChangeStateReadsReturnDefensiveDictionaryAndRecordSnapshots()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        var supplied = State(route, "42", "target-1", ScopeA);
        await repository.UpsertAsync(supplied, default);

        var first = await repository.GetBySourceIdsAsync(route, ["42"], default);
        var firstState = Assert.Single(first).Value;
        Assert.NotSame(supplied, firstState);
        Assert.NotSame(supplied.Route, firstState.Route);

        await repository.UpsertAsync(State(route, "42", "target-2", ScopeB), default);
        var second = await repository.GetBySourceIdsAsync(route, ["42"], default);
        var secondState = Assert.Single(second).Value;
        Assert.NotSame(first, second);
        Assert.NotSame(firstState, secondState);
        Assert.NotSame(firstState.Route, secondState.Route);
        Assert.Equal("target-1", firstState.TargetEntityId);
        Assert.Equal("target-2", secondState.TargetEntityId);
    }

    [Fact]
    public async Task ChangeStateOperationsHonorCancellationBeforeAccessingState()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.UpsertAsync(State(route, "42", "target-1", ScopeA), cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.GetBySourceIdsAsync(route, ["42"], cancelled));

        var result = await repository.GetBySourceIdsAsync(route, ["42"], default);
        Assert.Empty(result);
    }

    private static EntitySyncChangeStateRoute Route(string scope) =>
        EntitySyncChangeStateRoute.Create(
            "tenant-a",
            scope,
            "NetSuite",
            "source-connection",
            "Customer",
            "HaloPSA",
            "target-connection",
            "Client");

    private static EntitySyncChangeState State(
        EntitySyncChangeStateRoute route,
        string sourceEntityId,
        string targetEntityId,
        string payloadHash) =>
        new(
            route,
            sourceEntityId,
            $"Source {sourceEntityId}",
            targetEntityId,
            1,
            payloadHash,
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
}
