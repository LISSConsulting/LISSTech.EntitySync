using System.Reflection;
using System.Text.RegularExpressions;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Runtime;
using Npgsql;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntitySyncChangeStateRepositoryTests
{
    private const string ScopeA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ScopeB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CompleteRouteKey =
        "tenant_id, route_scope, source_vendor, source_connection_id, source_entity_type, " +
        "target_vendor, target_connection_id, target_entity_type, source_entity_key";
    private const int MaximumIndexedIdentityUtf8Bytes = 2000;



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

    [Fact]
    public async Task ChangeStateRouteIdentityIncludesTenantAndBothVendorConnectionTypeTriples()
    {
        var repository = new InMemoryEntitySyncChangeStateRepository();
        var route = Route(ScopeA);
        var variants = new[]
        {
            route with { TenantId = "tenant-b" },
            route with { SourceVendor = "N-central" },
            route with { SourceConnectionId = "source-connection-b" },
            route with { SourceEntityType = "Contact" },
            route with { TargetVendor = "N-central" },
            route with { TargetConnectionId = "target-connection-b" },
            route with { TargetEntityType = "Site" }
        };
        await repository.UpsertAsync(State(route, "42", "target-base", ScopeA), default);
        for (var index = 0; index < variants.Length; index++)
            await repository.UpsertAsync(State(variants[index], "42", $"target-route-{index}", ScopeA), default);

        var baseResult = await repository.GetBySourceIdsAsync(route, ["42"], default);
        Assert.Equal("target-base", Assert.Single(baseResult).Value.TargetEntityId);
        for (var index = 0; index < variants.Length; index++)
        {
            var variantResult = await repository.GetBySourceIdsAsync(variants[index], ["42"], default);
            Assert.Equal($"target-route-{index}", Assert.Single(variantResult).Value.TargetEntityId);
        }
    }

    [Fact]
    public void ChangeStateMigrationUsesBoundedStateColumnsAndCompleteRoutePrimaryKey()
    {
        var migration = CollapseWhitespace(ReadMigration(".002_entity_change_state.sql"));

        Assert.Contains("source_entity_key varchar(512) NOT NULL", migration);
        Assert.Contains("source_entity_id varchar(512) NOT NULL", migration);
        Assert.Contains("source_name varchar(512) NOT NULL", migration);
        Assert.Contains("target_entity_id varchar(512) NOT NULL", migration);
        Assert.Contains($"PRIMARY KEY ({CompleteRouteKey})", migration);
        Assert.Contains(IndexedIdentityCheck(), migration);
    }

    [Fact]
    public void ChangeStateRepairMigrationUpgradesPreviouslyAppliedNarrowPrimaryKey()
    {
        var migration = CollapseWhitespace(ReadMigration(".003_harden_entity_change_state_key.sql"));

        Assert.Contains("ALTER COLUMN source_entity_key TYPE varchar(512)", migration);
        Assert.Contains("ALTER COLUMN source_entity_id TYPE varchar(512)", migration);
        Assert.Contains("ALTER COLUMN source_name TYPE varchar(512)", migration);
        Assert.Contains("ALTER COLUMN target_entity_id TYPE varchar(512)", migration);
        Assert.Contains("DROP CONSTRAINT IF EXISTS entity_change_state_pkey", migration);
        Assert.Contains(
            $"ADD CONSTRAINT entity_change_state_pkey PRIMARY KEY ({CompleteRouteKey})",
            migration);
        Assert.Contains(IndexedIdentityCheck(), migration);

    }

    [Fact]
    public async Task PostgresUpsertBindsInvariantSourceKeyAndUsesCompleteConflictTarget()
    {
        await using var dataSource = CreateUnconnectedDataSource();
        var repository = new PostgresEntitySyncChangeStateRepository(dataSource);
        await using var command = CreatePostgresUpsertCommand(
            repository,
            State(Route(ScopeA), "ENTITY-I", "target-1", ScopeA));

        Assert.Equal("entity-i", command.Parameters["source_entity_key"].Value);
        Assert.Contains($"ON CONFLICT ({CompleteRouteKey})", CollapseWhitespace(command.CommandText));
        Assert.False(command.CommandText.Contains("lower(", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChangeStateRepositoriesAcceptExactStateFieldLimits()
    {
        var sourceEntityId = new string('S', 512);
        var sourceName = new string('N', 512);
        var targetEntityId = new string('T', 512);
        var state = State(Route(ScopeA), "source", "target", ScopeA) with
        {
            SourceEntityId = sourceEntityId,
            SourceName = sourceName,
            TargetEntityId = targetEntityId
        };
        var inMemory = new InMemoryEntitySyncChangeStateRepository();

        await inMemory.UpsertAsync(state, default);
        var result = await inMemory.GetBySourceIdsAsync(state.Route, [sourceEntityId], default);

        var stored = Assert.Single(result).Value;
        Assert.Equal(sourceName, stored.SourceName);
        Assert.Equal(targetEntityId, stored.TargetEntityId);
        await using var dataSource = CreateUnconnectedDataSource();
        var postgres = new PostgresEntitySyncChangeStateRepository(dataSource);
        await using var command = CreatePostgresUpsertCommand(postgres, state);
        Assert.Equal(sourceEntityId.ToLowerInvariant(), command.Parameters["source_entity_key"].Value);
        Assert.Equal(sourceEntityId, command.Parameters["source_entity_id"].Value);
        Assert.Equal(sourceName, command.Parameters["source_name"].Value);
        Assert.Equal(targetEntityId, command.Parameters["target_entity_id"].Value);
    }

    [Fact]
    public async Task ChangeStateRepositoriesRejectStateFieldsOverTheirLimitsBeforeMutation()
    {
        var valid = State(Route(ScopeA), "source", "target", ScopeA);
        var invalidStates = new[]
        {
            valid with { SourceEntityId = new string('S', 513) },
            valid with { SourceName = new string('N', 513) },
            valid with { TargetEntityId = new string('T', 513) }
        };
        var inMemory = new InMemoryEntitySyncChangeStateRepository();
        await using var dataSource = CreateUnconnectedDataSource();
        var postgres = new PostgresEntitySyncChangeStateRepository(dataSource);

        foreach (var invalidState in invalidStates)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => inMemory.UpsertAsync(invalidState, default));
            await Assert.ThrowsAsync<ArgumentException>(() => postgres.UpsertAsync(invalidState, default));
        }

        var stored = await inMemory.GetBySourceIdsAsync(valid.Route, ["source"], default);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task ChangeStateRepositoriesRejectSourceKeysOverTheSourceIdLimit()
    {
        var sourceEntityId = new string('S', 513);
        var route = Route(ScopeA);
        var inMemory = new InMemoryEntitySyncChangeStateRepository();
        await using var dataSource = CreateUnconnectedDataSource();
        var postgres = new PostgresEntitySyncChangeStateRepository(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            inMemory.GetBySourceIdsAsync(route, [sourceEntityId], default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            postgres.GetBySourceIdsAsync(route, [sourceEntityId], default));
    }
    [Fact]
    public async Task ChangeStateRepositoriesAcceptConservativeIndexedIdentityUtf8Boundary()
    {
        var route = MaximumMultibyteRoute();
        var sourceEntityId = new string('\u00e9', 328);
        var state = State(route, sourceEntityId, "target", ScopeA);
        var inMemory = new InMemoryEntitySyncChangeStateRepository();

        await inMemory.UpsertAsync(state, default);
        var result = await inMemory.GetBySourceIdsAsync(route, [sourceEntityId], default);

        Assert.Single(result);
        await using var dataSource = CreateUnconnectedDataSource();
        var postgres = new PostgresEntitySyncChangeStateRepository(dataSource);
        await using var command = CreatePostgresUpsertCommand(postgres, state);
        Assert.Equal(sourceEntityId, command.Parameters["source_entity_key"].Value);
    }

    [Fact]
    public async Task ChangeStateRepositoriesRejectIndexedIdentityOverUtf8LimitBeforeAccess()
    {
        var route = MaximumMultibyteRoute();
        var sourceEntityId = new string('\u00e9', 329);
        var state = State(route, sourceEntityId, "target", ScopeA);
        var inMemory = new InMemoryEntitySyncChangeStateRepository();
        await using var dataSource = CreateUnconnectedDataSource();
        var postgres = new PostgresEntitySyncChangeStateRepository(dataSource);

        var inMemoryWrite = await Assert.ThrowsAsync<ArgumentException>(() =>
            inMemory.UpsertAsync(state, default));
        var postgresWrite = await Assert.ThrowsAsync<ArgumentException>(() =>
            postgres.UpsertAsync(state, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            inMemory.GetBySourceIdsAsync(route, [sourceEntityId], default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            postgres.GetBySourceIdsAsync(route, [sourceEntityId], default));
        Assert.Contains($"{MaximumIndexedIdentityUtf8Bytes} UTF-8 bytes", inMemoryWrite.Message);
        Assert.Equal(inMemoryWrite.Message, postgresWrite.Message);
    }

    private static EntitySyncChangeStateRoute MaximumMultibyteRoute() =>
        EntitySyncChangeStateRoute.Create(
            new string('\u00e9', 256),
            ScopeA,
            new string('\u00e9', 64),
            new string('\u00e9', 64),
            new string('\u00e9', 64),
            new string('\u00e9', 64),
            new string('\u00e9', 64),
            new string('\u00e9', 64));

    private static string IndexedIdentityCheck() =>
        "CHECK (octet_length(tenant_id) + octet_length(route_scope) + " +
        "octet_length(source_vendor) + octet_length(source_connection_id) + " +
        "octet_length(source_entity_type) + octet_length(target_vendor) + " +
        "octet_length(target_connection_id) + octet_length(target_entity_type) + " +
        $"octet_length(source_entity_key) <= {MaximumIndexedIdentityUtf8Bytes})";


    private static NpgsqlDataSource CreateUnconnectedDataSource() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused;Username=unused;Password=unused");

    private static NpgsqlCommand CreatePostgresUpsertCommand(
        PostgresEntitySyncChangeStateRepository repository,
        EntitySyncChangeState state)
    {
        var method = typeof(PostgresEntitySyncChangeStateRepository).GetMethod(
            "CreateUpsertCommand",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<NpgsqlCommand>(method.Invoke(repository, [state]));
    }

    private static string ReadMigration(string suffix)
    {
        var assembly = typeof(PostgresEntitySyncChangeStateRepository).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim().Replace("( ", "(").Replace(" )", ")");

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
