using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ExpertOperationServiceTests
{
    [Fact]
    public async Task SuiteQl_accepts_one_thousand_and_uses_an_extra_row_as_truncation_evidence()
    {
        var adapter = new RecordingSuiteQlAdapter(1001);
        var runtime = new RecordingRuntimeFactory(adapter);
        var service = new ExpertOperationService(new DefinitionRepository(), runtime);

        var result = await service.ExecuteSuiteQlAsync(
            "tenant-a",
            "netsuite-main",
            "SELECT id FROM customer",
            1000,
            default);

        Assert.Equal(1000, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal(1001, adapter.LastMaximumRows);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, runtime.AcquireCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task SuiteQl_rejects_limits_outside_the_contract_before_acquiring_an_adapter(
        int maximumRows)
    {
        var adapter = new RecordingSuiteQlAdapter(1001);
        var runtime = new RecordingRuntimeFactory(adapter);
        var service = new ExpertOperationService(new DefinitionRepository(), runtime);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ExecuteSuiteQlAsync(
                "tenant-a",
                "netsuite-main",
                "SELECT id FROM customer",
                maximumRows,
                default));

        Assert.Equal("maximumRows", exception.ParamName);
        Assert.Equal(0, runtime.AcquireCalls);
        Assert.Equal(0, adapter.Calls);
    }

    private sealed class DefinitionRepository : IConnectionDefinitionRepository
    {
        public static readonly EntitySyncConnectionDefinition Definition = new(
            "tenant-a",
            "netsuite-main",
            "NetSuite",
            "NetSuite Main",
            1,
            true,
            new EntitySyncJsonValue("{}"),
            "ciphertext",
            DateTimeOffset.UnixEpoch,
            new EntitySyncActor("operator"),
            DateTimeOffset.UnixEpoch,
            new EntitySyncActor("operator"));

        public Task<EntitySyncConnectionDefinition?> GetAsync(
            string tenantId,
            string connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EntitySyncConnectionDefinition?>(Definition);

        public Task<EntitySyncConnectionDefinition> InsertAsync(
            string tenantId,
            EntitySyncConnectionDefinition definition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
            string tenantId,
            string? vendor,
            bool? enabled,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntitySyncConnectionDefinition?> TryReplaceAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            EntitySyncConnectionDefinition nextGeneration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRuntimeFactory(RecordingSuiteQlAdapter adapter)
        : IConnectionRuntimeFactory
    {
        public int AcquireCalls { get; private set; }

        public Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult<IConnectionRuntimeLease>(
                new Lease(adapter));
        }

        public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class Lease(RecordingSuiteQlAdapter adapter)
            : IConnectionRuntimeLease
        {
            public EntitySyncConnectionDefinition Definition =>
                DefinitionRepository.Definition;
            public IEntityAdapter Adapter { get; } = adapter;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSuiteQlAdapter(int rowCount)
        : IEntityAdapter, ISuiteQlExpertAdapter
    {
        private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            Enumerable.Range(1, rowCount)
                .Select(index => (IReadOnlyDictionary<string, object?>)
                    new Dictionary<string, object?> { ["id"] = index })
                .ToArray();

        public int Calls { get; private set; }
        public int? LastMaximumRows { get; private set; }
        public string Vendor => "NetSuite";
        public IReadOnlyList<string> LookupTypes => [];

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeSuiteQlAsync(
            string suiteQl,
            int maximumRows,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastMaximumRows = maximumRows;
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
