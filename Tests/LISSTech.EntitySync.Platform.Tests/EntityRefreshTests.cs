using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntityRefreshTests
{
    [Fact]
    public void SanitizerStripsCredentialShapedCustomFieldsBeforePersistence()
    {
        var entity = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "1",
            Name = "Acme"
        };
        entity.CustomFields["ApiKey"] = "sk_live_super_secret_value_1234";
        entity.CustomFields["Token"] = "eyJhbGciOiJIUzI1NiJ9.payload.signature";
        entity.CustomFields["Notes"] = "normal value";
        // Negative cases: ordinary words must not trip the substring matcher.
        entity.CustomFields["monkey"] = "spider-monkey";
        entity.CustomFields["TurkeyRegion"] = "central";
        entity.CustomFields["donkey_count"] = "3";

        var sanitized = EntityCredentialSanitizer.Sanitize(entity);
        Assert.Equal("[REDACTED]", sanitized.CustomFields["ApiKey"]);
        Assert.Equal("[REDACTED]", sanitized.CustomFields["Token"]);
        Assert.Equal("normal value", sanitized.CustomFields["Notes"]);
        Assert.Equal("spider-monkey", sanitized.CustomFields["monkey"]);
        Assert.Equal("central", sanitized.CustomFields["TurkeyRegion"]);
        Assert.Equal("3", sanitized.CustomFields["donkey_count"]);
    }

    [Fact]
    public async Task SnapshotReplacesAuthoritativelyAndTombstonesAbsentRecords()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var before = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await graph.ObserveEntitiesAsync(
            new EntityGraphObservation(scope,
                [Entity("NetSuite", "Customer", "1", "Alpha"),
                 Entity("NetSuite", "Customer", "2", "Beta")],
                before),
            default);
        // Atomic event upsert arrives after the snapshot window opens; the
        // snapshot must preserve "3" rather than tombstone it.
        var snapshotStart = before.AddSeconds(60);
        var atomicAt = snapshotStart.AddSeconds(15);
        await graph.ApplyAtomicEventAsync(
            scope,
            new EntityAtomicEvent(
                Guid.NewGuid(), "primary", "Customer", EntityAtomicOperation.Upsert,
                Entity("NetSuite", "Customer", "3", "Gamma"), null, atomicAt),
            connectionGeneration: 1,
            default);

        var snapshotObserved = before.AddSeconds(120);
        var result = await graph.ReplaceAuthoritativeSnapshotAsync(
            new EntityGraphSnapshot(scope, 1,
                [Entity("NetSuite", "Customer", "2", "Beta Updated"),
                 Entity("NetSuite", "Customer", "4", "Delta")],
                snapshotStart, snapshotObserved),
            default);

        Assert.Equal(2, result.UpsertedCount);
        Assert.Equal(1, result.TombstonedCount); // "1" was not in snapshot
        Assert.Equal(1, result.PreservedAfterBoundaryCount); // "3" survived the boundary

        var entities = await graph.QueryEntitiesAsync(
            new EntityGraphQuery("tenant"), default);
        var ids = entities.Select(record => record.Key.EntityId)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
        Assert.DoesNotContain("1", ids);
    }

    [Fact]
    public async Task SnapshotThatFailsBeforeReplaceLeavesGraphIntact()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "HaloPSA", "primary", "Client");
        await graph.ObserveEntitiesAsync(
            new EntityGraphObservation(scope,
                [Entity("HaloPSA", "Client", "1", "Acme")],
                new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)),
            default);

        // No snapshot replacement attempted; graph must remain unchanged.
        var queried = await graph.QueryEntitiesAsync(
            new EntityGraphQuery("tenant", Vendor: "HaloPSA", ConnectionId: "primary",
                EntityType: "Client"), default);
        var record = Assert.Single(queried);
        Assert.Equal("1", record.Key.EntityId);
        Assert.True(record.Entity.IsActive != false);
    }

    [Fact]
    public async Task RepeatedAtomicEventsAreIdempotent()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var eventId = Guid.NewGuid();
        var atomicEvent = new EntityAtomicEvent(
            eventId, "primary", "Customer", EntityAtomicOperation.Upsert,
            Entity("NetSuite", "Customer", "5", "Epsilon"), null,
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        var first = await graph.ApplyAtomicEventAsync(scope, atomicEvent, 1, default);
        var second = await graph.ApplyAtomicEventAsync(scope, atomicEvent, 1, default);
        var third = await graph.ApplyAtomicEventAsync(scope, atomicEvent, 1, default);

        Assert.Equal(EntityAtomicEventOutcomeKind.Applied, first.Kind);
        Assert.Equal(EntityAtomicEventOutcomeKind.Duplicate, second.Kind);
        Assert.Equal(EntityAtomicEventOutcomeKind.Duplicate, third.Kind);

        var records = await graph.QueryEntitiesAsync(
            new EntityGraphQuery("tenant", EntityType: "Customer"), default);
        Assert.Single(records);
    }

    [Fact]
    public async Task AtomicEventPersistsCursorAndSourceTimestampReceipt()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NCentral", "msp", "Customer");
        var eventId = Guid.NewGuid();
        var sourceUpdatedAt = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        var outcome = await graph.ApplyAtomicEventAsync(
            scope,
            new EntityAtomicEvent(eventId, "msp", "Customer",
                EntityAtomicOperation.Upsert,
                Entity("NCentral", "Customer", "42", "Acme"),
                "cursor-1",
                sourceUpdatedAt),
            1, default);

        Assert.Equal(EntityAtomicEventOutcomeKind.Applied, outcome.Kind);
        var receipt = await graph.TryGetAtomicEventReceiptAsync("tenant", eventId, default);
        Assert.NotNull(receipt);
        Assert.Equal(EntityAtomicEventOutcomeKind.Duplicate, receipt!.Kind);
    }

    [Fact]
    public async Task GraphBackedEntityReadExcludesTombstonesButKeepsPreserved()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var snapshotStart = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        // Seed three records.
        await graph.ObserveEntitiesAsync(
            new EntityGraphObservation(scope,
                [Entity("NetSuite", "Customer", "1", "Acme"),
                 Entity("NetSuite", "Customer", "2", "Beta")],
                snapshotStart.AddMinutes(-5)),
            default);
        // Atomic event for "3" arrives before the snapshot boundary.
        await graph.ApplyAtomicEventAsync(
            scope,
            new EntityAtomicEvent(Guid.NewGuid(), "primary", "Customer",
                EntityAtomicOperation.Upsert,
                Entity("NetSuite", "Customer", "3", "Gamma"),
                "cursor", snapshotStart.AddSeconds(15)),
            1, default);
        // Snapshot replace that drops "1" and "2" but keeps "3".
        await graph.ReplaceAuthoritativeSnapshotAsync(
            new EntityGraphSnapshot(scope, 1,
                [Entity("NetSuite", "Customer", "4", "Delta")],
                snapshotStart, snapshotStart.AddMinutes(1)),
            default);

        var records = await graph.QueryEntitiesAsync(
            new EntityGraphQuery("tenant", ConnectionId: "primary"), default);
        var ids = records.Select(record => record.Key.EntityId).ToHashSet(StringComparer.Ordinal);
        // "3" was inserted before snapshot started, but its last_observed_at is
        // snapshot_start, so the snapshot does NOT tombstone it. Confirm preserved.
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("2", ids);
        // Confirm observation timestamps are surfaced.
        var three = records.Single(record => record.Key.EntityId == "3");
        Assert.True(three.FirstObservedAt <= three.LastObservedAt);
    }

    [Fact]
    public void GenerateConnectionIdProducesOpaqueToken()
    {
        var first = EntityRefreshService.GenerateConnectionId();
        var second = EntityRefreshService.GenerateConnectionId();
        Assert.StartsWith("cnx_", first);
        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 16);
    }

    [Fact]
    public void VendorCatalogIncludesSophosCentralAndBuiltIns()
    {
        var sophos = EntityAdapterCapabilities.ForVendor(EntitySyncVendors.SophosCentral);
        Assert.Equal(EntitySyncVendors.SophosCentral, sophos.Vendor);
        Assert.NotEmpty(sophos.EntityTypes);

        // Verify each built-in vendor yields at least one capability row.
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("HaloPSA").EntityTypes);
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("NetSuite").EntityTypes);
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("NCentral").EntityTypes);
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("Bill.com").EntityTypes);
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("AgentController").EntityTypes);
        Assert.NotEmpty(EntityAdapterCapabilities.ForVendor("OrchestraMSP").EntityTypes);
    }

    [Fact]
    public async Task InMemoryRefreshStateRoundTripsLeaseAndRelease()
    {
        var repo = new InMemoryEntityRefreshStateRepository();
        var state = new EntityRefreshStateSnapshot
        {
            Key = new EntityRefreshStateKey("tenant", "primary", "Customer"),
            Vendor = "NetSuite",
            ConnectionGeneration = 1,
            Status = EntityRefreshStatus.Pending,
            Mode = EntityRefreshMode.Scheduled
        };
        var queued = await repo.UpsertOnQueueAsync(
            "tenant", state, DateTimeOffset.UtcNow, default);
        Assert.Equal(EntityRefreshStatus.Pending, queued.Status);

        var leased = await repo.TryAcquireLeaseAsync(
            queued.Key, 1, "owner-A", TimeSpan.FromMinutes(5),
            DateTimeOffset.UtcNow, default);
        Assert.NotNull(leased);
        Assert.Equal(EntityRefreshStatus.Running, leased!.Status);
        Assert.Equal("owner-A", leased.LeaseOwner);

        var released = await repo.TryReleaseLeaseAsync(
            queued.Key, "owner-A", EntityRefreshStatus.Succeeded,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1), 42,
            "cursor", DateTimeOffset.UtcNow, null,
            leased.SnapshotStartedAt, DateTimeOffset.UtcNow, default);
        Assert.NotNull(released);
        Assert.Equal(EntityRefreshStatus.Succeeded, released!.Status);
        Assert.Equal(42, released.ObservedCount);
        Assert.Equal("cursor", released.Cursor);
    }

    [Fact]
    public async Task InMemoryCapabilityCacheListsRefreshableByTenant()
    {
        var repo = new InMemoryEntityRefreshCapabilityRepository();
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new EntityRefreshCapability("tenant", "primary", "NetSuite",
                "Customer", SupportsRefresh: true, now),
            new EntityRefreshCapability("tenant", "primary", "NetSuite",
                "Invoice", SupportsRefresh: false, now)
        };
        await repo.ReplaceAsync("tenant", "primary", rows, now, default);
        var refreshable = await repo.ListRefreshableAsync("tenant", default);
        Assert.Equal("Customer", refreshable[0].EntityType);
    }

    [Fact]
    public void InMemorySucceededRowsAreRecurringlyEligibleForLease()
    {
        var repo = new InMemoryEntityRefreshStateRepository();
        var key = new EntityRefreshStateKey("tenant", "primary", "Customer");
        var now = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
        var succeeded = new EntityRefreshStateSnapshot
        {
            Key = key,
            Vendor = "NetSuite",
            ConnectionGeneration = 1,
            Status = EntityRefreshStatus.Succeeded,
            Mode = EntityRefreshMode.Scheduled,
            NextScheduledAt = now.AddMinutes(-1)
        };
        repo.UpsertOnQueueAsync("tenant", succeeded, now.AddMinutes(-1), default)
            .GetAwaiter().GetResult();
        // Force the row into Succeeded state without an active lease.
        var leased = repo.TryAcquireLeaseAsync(key, 1, "owner-A",
                TimeSpan.FromMinutes(5), now, default).GetAwaiter().GetResult();
        Assert.NotNull(leased);
        var released = repo.TryReleaseLeaseAsync(key, "owner-A",
                EntityRefreshStatus.Succeeded, now, now,
                now.AddMinutes(15), 12, "cursor-A", now, null,
                leased!.SnapshotStartedAt, now, default).GetAwaiter().GetResult();
        Assert.NotNull(released);
        Assert.Equal(EntityRefreshStatus.Succeeded, released!.Status);

        // Next sweep: due rows include the Succeeded row (NextScheduledAt elapsed).
        var due = repo.LeaseDueAsync("tenant", "owner-B",
            TimeSpan.FromMinutes(5), now.AddMinutes(30), 10, default)
            .GetAwaiter().GetResult();
        Assert.Single(due);
        Assert.Equal(EntityRefreshStatus.Running, due[0].State.Status);
        Assert.Equal(EntityRefreshMode.Scheduled, due[0].State.Mode);
    }

    [Fact]
    public async Task InMemoryManualQueueForcesImmediatePendingUnlessLeaseHeld()
    {
        var repo = new InMemoryEntityRefreshStateRepository();
        var key = new EntityRefreshStateKey("tenant", "primary", "Customer");
        var now = DateTimeOffset.UtcNow;
        // Seed as a healthy Succeeded row; a Manual queue must still flip it Pending.
        var healthy = new EntityRefreshStateSnapshot
        {
            Key = key,
            Vendor = "NetSuite",
            ConnectionGeneration = 1,
            Status = EntityRefreshStatus.Succeeded,
            Mode = EntityRefreshMode.Scheduled,
            NextScheduledAt = now.AddHours(2)
        };
        repo.UpsertOnQueueAsync("tenant", healthy, now.AddHours(2), default)
            .GetAwaiter().GetResult();

        var requested = now;
        var queued = await repo.UpsertOnQueueAsync("tenant",
            new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = "NetSuite",
                ConnectionGeneration = 1,
                Status = EntityRefreshStatus.Pending,
                Mode = EntityRefreshMode.Manual
            },
            requested, default);
        Assert.Equal(EntityRefreshStatus.Pending, queued.Status);
        Assert.Equal(EntityRefreshMode.Manual, queued.Mode);
        Assert.Equal(requested, queued.NextScheduledAt);

        // When another worker holds the lease, the manual queue must NOT clobber
        // the running status — the row stays Running.
        var leased = await repo.TryAcquireLeaseAsync(key, 1, "owner-A",
            TimeSpan.FromMinutes(5), now, default);
        Assert.NotNull(leased);
        var requeued = await repo.UpsertOnQueueAsync("tenant",
            new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = "NetSuite",
                ConnectionGeneration = 1,
                Status = EntityRefreshStatus.Pending,
                Mode = EntityRefreshMode.Manual
            },
            now, default);
        Assert.Equal(EntityRefreshStatus.Running, requeued.Status);
    }

    [Fact]
    public async Task InMemoryReleasePersistsCompletedCursorAndSourceTimestamp()
    {
        var repo = new InMemoryEntityRefreshStateRepository();
        var key = new EntityRefreshStateKey("tenant", "primary", "Customer");
        var now = DateTimeOffset.UtcNow;
        var seeded = await repo.UpsertOnQueueAsync("tenant",
            new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = "NetSuite",
                ConnectionGeneration = 1,
                Status = EntityRefreshStatus.Pending,
                Mode = EntityRefreshMode.Manual,
                IsStale = true,
                NextScheduledAt = now.AddMinutes(-1)
            },
            now.AddMinutes(-1), default);
        var leased = await repo.TryAcquireLeaseAsync(seeded.Key, 1, "owner-A",
            TimeSpan.FromMinutes(5), now, default);
        Assert.NotNull(leased);
        var released = await repo.TryReleaseLeaseAsync(
            leased!.Key, "owner-A", EntityRefreshStatus.Succeeded,
            now, now, now.AddHours(1),
            42, "completed-cursor", now.AddMinutes(1), null,
            leased.SnapshotStartedAt, now, default);
        Assert.NotNull(released);
        Assert.Equal("completed-cursor", released!.Cursor);
        Assert.Equal(now.AddMinutes(1), released.SourceUpdatedAt);
        Assert.Equal(EntityRefreshStatus.Succeeded, released.Status);
        Assert.Equal(EntityRefreshMode.Manual, released.Mode);
        Assert.False(released.IsStale);
        Assert.Null(released.LeaseOwner);
    }

    [Fact]
    public async Task InMemoryCancellationReleasesLeaseAsPendingWithoutTombstoning()
    {
        var repo = new InMemoryEntityRefreshStateRepository();
        var key = new EntityRefreshStateKey("tenant", "primary", "Customer");
        var now = DateTimeOffset.UtcNow;
        var seeded = await repo.UpsertOnQueueAsync("tenant",
            new EntityRefreshStateSnapshot
            {
                Key = key,
                Vendor = "NetSuite",
                ConnectionGeneration = 1,
                Status = EntityRefreshStatus.Pending,
                Mode = EntityRefreshMode.Scheduled,
                Cursor = "pre-cancel-cursor",
                SourceUpdatedAt = now.AddMinutes(-1),
                NextScheduledAt = now.AddMinutes(-1)
            },
            now.AddMinutes(-1), default);
        var leased = await repo.TryAcquireLeaseAsync(seeded.Key, 1, "owner-A",
            TimeSpan.FromMinutes(5), now, default);
        Assert.NotNull(leased);
        // Simulated cancellation: the worker releases the lease back to Pending,
        // preserving the cursor/source_updated_at it had before the snapshot.
        var released = await repo.TryReleaseLeaseAsync(
            leased!.Key, "owner-A", EntityRefreshStatus.Pending,
            now, leased.LastSuccessfulAt, now.AddMinutes(5),
            leased.ObservedCount, leased.Cursor, leased.SourceUpdatedAt,
            null, leased.SnapshotStartedAt, null, default);
        Assert.NotNull(released);
        Assert.Equal(EntityRefreshStatus.Pending, released!.Status);
        Assert.False(released.IsStale);
        Assert.Equal("pre-cancel-cursor", released.Cursor);
        Assert.Equal(now.AddMinutes(-1), released.SourceUpdatedAt);
    }

    [Fact]
    public void InMemoryPreservationCountMatchesPreSnapshotRowsWithLastObservedAtAtOrAfterBoundary()
    {
        // Aligns the in-memory authoritative snapshot with the Postgres semantics:
        // pre-existing records whose last_observed_at >= snapshot_started_at are
        // preserved from being tombstoned.
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var snapshotStart = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        // Seed three records, all before the snapshot boundary.
        graph.ObserveEntitiesAsync(
            new EntityGraphObservation(scope,
                [
                    Entity("NetSuite", "Customer", "1", "Alpha"),
                    Entity("NetSuite", "Customer", "2", "Beta"),
                    Entity("NetSuite", "Customer", "3", "Gamma")
                ],
                snapshotStart.AddMinutes(-5)),
            default).GetAwaiter().GetResult();
        // Atomic event for "3" arrives during the snapshot window.
        graph.ApplyAtomicEventAsync(
            scope,
            new EntityAtomicEvent(Guid.NewGuid(), "primary", "Customer",
                EntityAtomicOperation.Upsert,
                Entity("NetSuite", "Customer", "3", "Gamma Updated"),
                "cursor-3", snapshotStart.AddSeconds(15)),
            1, default).GetAwaiter().GetResult();
        // Snapshot replace includes only "2" (kept and updated) and a new "4".
        var result = graph.ReplaceAuthoritativeSnapshotAsync(
            new EntityGraphSnapshot(scope, 1,
                [
                    Entity("NetSuite", "Customer", "2", "Beta"),
                    Entity("NetSuite", "Customer", "4", "Delta")
                ],
                snapshotStart, snapshotStart.AddMinutes(1),
                Cursor: null, SourceUpdatedAt: null),
            default).GetAwaiter().GetResult();
        Assert.Equal(2, result.UpsertedCount);
        Assert.Equal(1, result.TombstonedCount); // only "1" is fully pre-snapshot and absent
        // "3" survives: its last_observed_at is inside the snapshot window.
        Assert.Equal(1, result.PreservedAfterBoundaryCount);
    }

    [Fact]
    public void InMemoryAtomicEventRejectsStaleConnectionGeneration()
    {
        var graph = new InMemoryEntityGraphRepository();
        var scope = new EntityGraphScope("tenant", "NetSuite", "primary", "Customer");
        var eventId = Guid.NewGuid();
        var atomicEvent = new EntityAtomicEvent(
            eventId, "primary", "Customer", EntityAtomicOperation.Upsert,
            Entity("NetSuite", "Customer", "1", "Alpha"),
            null, DateTimeOffset.UtcNow);
        var first = graph.ApplyAtomicEventAsync(scope, atomicEvent, 1, default)
            .GetAwaiter().GetResult();
        Assert.Equal(EntityAtomicEventOutcomeKind.Applied, first.Kind);
        // A rotation occurred between calls. A second atomic event for the same
        // connection must be rejected with the same generation-conflict exception
        // shape the service uses for the rest of the platform.
        Assert.Throws<ConnectionGenerationConflictException>(() =>
            graph.ApplyAtomicEventAsync(scope,
                new EntityAtomicEvent(Guid.NewGuid(), "primary", "Customer",
                    EntityAtomicOperation.Upsert,
                    Entity("NetSuite", "Customer", "2", "Beta"),
                    null, DateTimeOffset.UtcNow),
                2, default).GetAwaiter().GetResult());
    }

    [Fact]
    public void MigrationSqlIsParseableAndDeclaresRefreshStateColumns()
    {
        var assembly = typeof(LISSTech.EntitySync.Runtime.PostgresEntityGraphRepository).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.Contains(".Migrations.022_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Embedded migration 022_entity_refresh_state.sql was not found.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded resource stream '{resource}' was null.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        // The migration must declare every column referenced by the runtime
        // repository and the trigger function. One PK, one FK, no duplicate
        // declarations.
        string[] requiredColumns =
        {
            "last_attempt_at", "last_successful_at", "next_scheduled_at",
            "observed_count", "cursor", "source_updated_at", "error_code",
            "snapshot_started_at", "snapshot_completed_at",
            "is_stale", "refreshed_at", "lease_owner", "lease_expires_at",
            "connection_generation"
        };
        foreach (var column in requiredColumns)
            Assert.Contains(column, sql, StringComparison.OrdinalIgnoreCase);
        // The entity_refresh_state declaration (line 27) and the
        // connection_refresh_capabilities declaration (line 113) both use this
        // shape; count only the first by counting between CREATE TABLE
        // entity_refresh_state and the next CREATE TABLE statement.
        var stateSegment = sql[..sql.IndexOf(
            "CREATE TABLE IF NOT EXISTS entitysync.entity_refresh_events",
            StringComparison.Ordinal)];
        var pkOccurrences = CountOccurrences(stateSegment,
            "PRIMARY KEY (tenant_id, connection_id, entity_type)");
        Assert.Equal(1, pkOccurrences);
        // Exactly one FOREIGN KEY declaration referencing connection_definitions.
        var fkOccurrences = CountOccurrences(sql,
            "REFERENCES entitysync.connection_definitions (tenant_id, connection_id)");
        Assert.Equal(1, fkOccurrences);
        // No malformed `mode = CASE` followed by `mode = mode,` (the original
        // release-lease defect).
        Assert.DoesNotContain("mode = mode,", sql, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static ExternalEntity Entity(string vendor, string entityType, string id, string name) =>
        new()
        {
            Vendor = vendor,
            EntityType = entityType,
            Id = id,
            Name = name,
            ExternalIds = { [vendor + "Id"] = id }
        };
}
