using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using Xunit;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class CanonicalChangeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Identical_outbox_replay_returns_same_receipt_and_work_ids()
    {
        var repository = new MemoryCanonicalRepository();
        var signal = new RecordingSignal();
        var service = new CanonicalChangeService(repository, signal, new FixedTimeProvider(Now));
        var request = Request("om-event-42", 7, Hash('a'));

        var first = await service.AcceptAsync(request, default);
        var replay = await service.AcceptAsync(request, default);

        Assert.Equal(first, replay);
        Assert.Equal(2, first.WorkIds.Count);
        Assert.Equal(1, repository.InsertCount);
        Assert.Equal(2, signal.Notifications);
    }

    [Fact]
    public async Task Conflicting_outbox_replay_is_rejected_as_409_conflict()
    {
        var repository = new MemoryCanonicalRepository();
        var service = new CanonicalChangeService(
            repository, new RecordingSignal(), new FixedTimeProvider(Now));
        await service.AcceptAsync(Request("om-event-42", 7, Hash('a')), default);

        var error = await Assert.ThrowsAsync<CanonicalChangeConflictException>(() =>
            service.AcceptAsync(Request("om-event-42", 8, Hash('b')), default));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Canonical_read_must_match_asserted_UUID_and_version_or_hold()
    {
        var adapter = new FakeCanonicalVersionAdapter(
            new CanonicalEntityVersion(
                Guid.Parse("11111111-1111-1111-1111-111111111111"), 8,
                new ExternalEntity { Vendor = "OrchestraMSP", EntityType = "Client", Id = "11111111-1111-1111-1111-111111111111", Name = "Acme" }));
        var request = Request("om-event-42", 7, Hash('a'));

        var result = await CanonicalChangeService.ReadAssertedVersionAsync(adapter, request, default);

        Assert.Equal(CanonicalVersionReadStatus.StaleVersion, result.Status);
        Assert.Null(result.Entity);
        Assert.Equal(1, adapter.Reads);
    }

    [Fact]
    public async Task Canonical_read_returns_exact_asserted_version_without_recomputing_latest()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var entity = new ExternalEntity
        {
            Vendor = "OrchestraMSP", EntityType = "Client", Id = id.ToString(), Name = "Acme"
        };
        var adapter = new FakeCanonicalVersionAdapter(new CanonicalEntityVersion(id, 7, entity));

        var result = await CanonicalChangeService.ReadAssertedVersionAsync(
            adapter, Request("om-event-42", 7, Hash('a')), default);

        Assert.Equal(CanonicalVersionReadStatus.Exact, result.Status);
        Assert.Same(entity, result.Entity);
        Assert.Equal(7, adapter.RequestedVersion);
    }

    private static CanonicalChangeRequest Request(
        string eventId, long version, EntitySyncSha256 hash) =>
        new(
            "tenant", eventId, "Client",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            version, ["Name", "Email"], hash, Now.AddMinutes(-1));

    private static EntitySyncSha256 Hash(char value) => new(new string(value, 64));

    [Fact]
    public async Task Pinned_canonical_snapshot_is_planned_without_rereading_latest()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var pinned = new ExternalEntity
        {
            Vendor = "OrchestraMSP",
            EntityType = "Client",
            Id = id.ToString("D"),
            Name = "version-7"
        };
        using var connections = new InMemoryEntityConnectionRepository();
        var source = new CountingAdapter("OrchestraMSP", []);
        connections.Register("tenant", "source", source);
        connections.Register("tenant", "target", new CountingAdapter("HaloPSA", []));
        var planner = new EntitySyncPlanner(
            connections,
            new InMemoryEntitySyncPlanRepository(),
            new InMemoryEntityExclusionRepository(),
            new WeightedEntityMatcher(),
            new DefaultEntityMapper(),
            new InMemoryEntitySyncChangeStateRepository());

        var plan = await planner.CreateAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "OrchestraMSP",
            SourceConnectionId = "source",
            SourceEntityType = "Client",
            SourceEntityId = id.ToString("D"),
            PinnedCanonicalSource = new CanonicalEntityVersion(id, 7, pinned),
            TargetVendor = "HaloPSA",
            TargetConnectionId = "target",
            TargetEntityType = "Client"
        }, default);

        Assert.Equal(0, source.EntityReads);
        Assert.Single(plan.Items);
        Assert.Equal("version-7", plan.Items[0].Source.Name);
    }

    private sealed class MemoryCanonicalRepository : ICanonicalChangeRepository
    {
        private readonly Dictionary<string, (CanonicalChangeRequest Request, CanonicalChangeReceipt Receipt)> receipts = [];
        public int InsertCount { get; private set; }

        public Task<CanonicalChangeReceipt> AcceptAsync(
            CanonicalChangeRequest request,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken)
        {
            if (receipts.TryGetValue(request.OutboxEventId, out var existing))
            {
                if (existing.Request.CanonicalEntityId != request.CanonicalEntityId
                    || existing.Request.CanonicalVersion != request.CanonicalVersion
                    || existing.Request.PayloadSha256 != request.PayloadSha256
                    || !existing.Request.ChangedFields.SequenceEqual(request.ChangedFields, StringComparer.OrdinalIgnoreCase))
                    throw new CanonicalChangeConflictException(request.OutboxEventId);
                return Task.FromResult(existing.Receipt);
            }

            InsertCount++;
            var receipt = new CanonicalChangeReceipt(
                Guid.NewGuid(), request.TenantId, request.OutboxEventId,
                request.CanonicalEntityId, request.CanonicalVersion, request.PayloadSha256,
                [Guid.NewGuid(), Guid.NewGuid()], receivedAt);
            receipts.Add(request.OutboxEventId, (request, receipt));
            return Task.FromResult(receipt);
        }
    }

    private sealed class RecordingSignal : IEntitySyncWorkSignal
    {
        public int Notifications { get; private set; }
        public Task NotifyAsync(CancellationToken cancellationToken)
        {
            Notifications++;
            return Task.CompletedTask;
        }
        public Task WaitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class FakeCanonicalVersionAdapter(CanonicalEntityVersion result)
        : ICanonicalEntityVersionAdapter
    {
        public int Reads { get; private set; }
        public long RequestedVersion { get; private set; }
        public Task<CanonicalEntityVersion?> ReadCanonicalAsync(
            string entityType,
            Guid canonicalEntityId,
            long assertedVersion,
            CancellationToken cancellationToken)
        {
            Reads++;
            RequestedVersion = assertedVersion;
            return Task.FromResult<CanonicalEntityVersion?>(result);
        }
    }

    private sealed class CountingAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity> entities) : IEntityAdapter
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int EntityReads { get; private set; }
        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query, CancellationToken cancellationToken)
        {
            EntityReads++;
            return Task.FromResult(entities);
        }
        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
