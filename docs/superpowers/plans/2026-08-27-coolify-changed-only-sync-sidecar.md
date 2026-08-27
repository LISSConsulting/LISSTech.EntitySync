# Coolify Changed-Only Sync Sidecar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a hardened Coolify sidecar that immediately and then every 12 hours updates only persistently linked NetSuite customers whose mapped HaloPSA payload changed since the last successful write.

**Architecture:** A new .NET 8 scheduler executable reuses EntitySync planning, mapping, approval, and apply services directly. PostgreSQL stores canonical desired-payload hashes and supplies a route advisory lock; a new shared Hosting project owns server-managed adapter construction for both MCP and scheduler. The scheduler creates a fresh changed-linked-only plan per run, inspects and approves it, checkpoints successful writes, exposes bounded health/status, and never retries before the next normal interval.

**Tech Stack:** .NET 8, C# 12, ASP.NET Core minimal hosting, Npgsql 8, PostgreSQL 18, Docker Compose/Coolify, OpenTelemetry OTLP, xUnit 2.9.

## Global Constraints

- Fixed route: NetSuite `Customer` connection `netsuite` to HaloPSA `Client` connection `halopsa`.
- Include active and inactive NetSuite customers.
- First run reconciles linked customers; later runs write only changed mapped payloads.
- Only `Update` items with `MatchType=Linked` may write unattended.
- `Create`, fuzzy `Link`, `Review`, missing-target, and unmatched rows must never write.
- Run immediately after startup, then 12 hours after each run completes.
- No immediate retries; failed/uncheckpointed items become eligible at the next scheduled run.
- PostgreSQL is authoritative for hashes and cross-replica locking; no scheduler volume.
- Detect NetSuite mapped-payload changes only; do not add HaloPSA drift reconciliation.
- Keep existing standard planning, MCP approval, exclusion, connection-generation, and single-consumption behavior unchanged.
- Never log or expose credentials, tokens, raw vendor responses, mapped payloads, or entity names.
- The container remains non-root, read-only, capability-free, `no-new-privileges`, and tmpfs-only.

---

### Task 1: Change-State Contracts and Canonical Write Digest

**Files:**
- Create: `src/Core/EntitySyncUpdatePolicy.cs`
- Create: `src/Core/EntitySyncChangeState.cs`
- Create: `src/Ports/IEntitySyncChangeStateRepository.cs`
- Create: `src/Application/EntityWriteRequestDigest.cs`
- Modify: `src/Application/EntitySyncRequests.cs:3-21`
- Modify: `src/Core/EntitySyncPlanExecution.cs:3-10`
- Modify: `src/Core/EntitySyncPlanItem.cs:3-12`
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/EntityWriteRequestDigestTests.cs`

**Interfaces:**
- Produces: `EntitySyncUpdatePolicy.Standard` and `EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly`.
- Produces: `EntitySyncChangeStateRoute.Create(string tenantId, string scope, string sourceVendor, string sourceConnectionId, string sourceEntityType, string targetVendor, string targetConnectionId, string targetEntityType)`, `EntitySyncChangeState`, and `IEntitySyncChangeStateRepository`.
- Produces: `EntityWriteRequestDigest.SchemaVersion` and `Compute(EntityWriteRequest request)`.
- Extends: `CreateEntitySyncPlanRequest.UpdatePolicy`, `.ChangeStateScope`; `EntitySyncPlanExecution.UpdatePolicy`, `.ChangeStateScope`; `EntitySyncPlanItem.DesiredStateHash`, `.DesiredStateHashVersion`.

- [ ] **Step 1: Write failing canonical digest tests**

Create tests that build two logically identical requests with reversed dictionary insertion order and one request per changed field:

```csharp
[Fact]
public void DigestIsStableAcrossDictionaryInsertionOrder()
{
    var first = Request(
        fields: new Dictionary<string, object?> { ["website"] = "https://example.test", ["address"] = new Dictionary<string, object?> { ["city"] = "Toronto", ["line1"] = "1 Main" } },
        customFields: new Dictionary<string, string?> { ["CFNetSuiteCustomerName"] = "Acme", ["CFNetSuiteCustomerID"] = "42" });
    var second = Request(
        fields: new Dictionary<string, object?> { ["address"] = new Dictionary<string, object?> { ["line1"] = "1 Main", ["city"] = "Toronto" }, ["website"] = "https://example.test" },
        customFields: new Dictionary<string, string?> { ["CFNetSuiteCustomerID"] = "42", ["CFNetSuiteCustomerName"] = "Acme" });

    Assert.Equal(EntityWriteRequestDigest.Compute(first), EntityWriteRequestDigest.Compute(second));
}

[Theory]
[InlineData("name")]
[InlineData("target-id")]
[InlineData("primary-site-id")]
[InlineData("field")]
[InlineData("custom-field")]
public void DigestChangesWhenMappedWriteChanges(string mutation)
{
    var baseline = Request();
    var changed = Request();
    Mutate(changed, mutation);

    Assert.NotEqual(EntityWriteRequestDigest.Compute(baseline), EntityWriteRequestDigest.Compute(changed));
}
```

- [ ] **Step 2: Run digest tests and verify red**

Run:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntityWriteRequestDigestTests
```

Expected: compilation fails because the digest and change-state types do not exist.

- [ ] **Step 3: Add update policy and digest-covered plan fields**

Create:

```csharp
namespace LISSTech.EntitySync.Core;

public enum EntitySyncUpdatePolicy
{
    Standard = 0,
    ChangedLinkedUpdatesOnly = 1
}
```

Add to the request and execution types:

```csharp
public EntitySyncUpdatePolicy UpdatePolicy { get; init; } = EntitySyncUpdatePolicy.Standard;
public string? ChangeStateScope { get; init; }
```

Use mutable setters in `EntitySyncPlanExecution`. Add to `EntitySyncPlanItem`:

```csharp
public string? DesiredStateHash { get; set; }
public int? DesiredStateHashVersion { get; set; }
```

`EntitySyncPlanDigest` already serializes `Execution` and `Items`; no parallel digest convention is added.

- [ ] **Step 4: Add validated change-state route and repository contract**

Create records with exact contracts:

```csharp
public sealed record EntitySyncChangeStateRoute(
    string TenantId,
    string Scope,
    string SourceVendor,
    string SourceConnectionId,
    string SourceEntityType,
    string TargetVendor,
    string TargetConnectionId,
    string TargetEntityType)
{
    public static EntitySyncChangeStateRoute Create(
        string tenantId,
        string scope,
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType);
}

public sealed record EntitySyncChangeState(
    EntitySyncChangeStateRoute Route,
    string SourceEntityId,
    string SourceName,
    string TargetEntityId,
    int HashVersion,
    string PayloadHash,
    DateTimeOffset AppliedAt);
```

The factory trims values, normalizes vendors through `EntitySyncVendors.Normalize`, rejects blanks, caps IDs/scope/type/name inputs consistently with existing exclusion routes, and requires a lowercase 64-character hexadecimal scope/hash where applicable.

Create the port:

```csharp
public interface IEntitySyncChangeStateRepository
{
    Task<IReadOnlyDictionary<string, EntitySyncChangeState>> GetBySourceIdsAsync(
        EntitySyncChangeStateRoute route,
        IReadOnlyCollection<string> sourceEntityIds,
        CancellationToken cancellationToken);

    Task UpsertAsync(EntitySyncChangeState state, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Implement canonical recursive serialization**

`EntityWriteRequestDigest.Compute` must serialize an ordered anonymous/document model containing schema version, vendor, entity type, ID, primary site ID, name, fields, and custom fields. Recursively convert dictionaries to `SortedDictionary<string, object?>` with `StringComparer.Ordinal`; convert enumerable values in order; format numbers using invariant culture; preserve null and Boolean types. Hash UTF-8 JSON with SHA-256 and return lowercase hexadecimal.

```csharp
public static class EntityWriteRequestDigest
{
    public const int SchemaVersion = 1;

    public static string Compute(EntityWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = SchemaVersion,
            ["vendor"] = request.Vendor,
            ["entityType"] = request.EntityType,
            ["id"] = request.Id,
            ["primarySiteId"] = request.PrimarySiteId,
            ["name"] = request.Name,
            ["fields"] = Canonicalize(request.Fields),
            ["customFields"] = Canonicalize(request.CustomFields)
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
```

- [ ] **Step 6: Run focused tests**

Run the Task 1 filter. Expected: all digest and route-validation tests pass.

- [ ] **Step 7: Commit contracts and digest**

```bash
git add src/Core src/Ports/IEntitySyncChangeStateRepository.cs src/Application/EntitySyncRequests.cs src/Application/EntityWriteRequestDigest.cs Tests/LISSTech.EntitySync.Platform.Tests/EntityWriteRequestDigestTests.cs
git commit -m "feat: define changed-only sync state"
```

---

### Task 2: PostgreSQL and In-Memory Change-State Repositories

**Files:**
- Create: `db/migrations/002_entity_change_state.sql`
- Create: `src/Runtime/InMemoryEntitySyncChangeStateRepository.cs`
- Create: `src/Runtime/PostgresEntitySyncChangeStateRepository.cs`
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs`

**Interfaces:**
- Consumes: `IEntitySyncChangeStateRepository`, `EntitySyncChangeStateRoute`, `EntitySyncChangeState` from Task 1.
- Produces: thread-safe in-memory and PostgreSQL repository implementations.
- Uses: existing `EntitySyncDatabaseMigrator.ApplyAsync` and embedded migration loading.

- [ ] **Step 1: Write failing in-memory repository contract tests**

Cover route isolation, case-insensitive source identity, batch reads, replacement, defensive snapshots, and cancellation:

```csharp
[Fact]
public async Task ChangeStateUpsertReplacesOnlyTheSameRouteAndSource()
{
    var repository = new InMemoryEntitySyncChangeStateRepository();
    var route = Route("scope-a");
    await repository.UpsertAsync(State(route, "42", "target-1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), default);
    await repository.UpsertAsync(State(route, "42", "target-2", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), default);
    await repository.UpsertAsync(State(Route("scope-b"), "42", "target-3", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), default);

    var result = await repository.GetBySourceIdsAsync(route, ["42"], default);

    Assert.Equal("target-2", Assert.Single(result).Value.TargetEntityId);
}
```

- [ ] **Step 2: Run repository tests and verify red**

Run the `EntitySyncChangeStateRepositoryTests` filter. Expected: compilation fails because repository implementations do not exist.

- [ ] **Step 3: Add the migration**

Create a table with bounded columns and one primary key:

```sql
CREATE TABLE IF NOT EXISTS entitysync.entity_change_state (
    tenant_id text NOT NULL,
    route_scope char(64) NOT NULL,
    source_vendor text NOT NULL,
    source_connection_id text NOT NULL,
    source_entity_type text NOT NULL,
    target_vendor text NOT NULL,
    target_connection_id text NOT NULL,
    target_entity_type text NOT NULL,
    source_entity_key text NOT NULL,
    source_entity_id text NOT NULL,
    source_name text NOT NULL,
    target_entity_id text NOT NULL,
    hash_version integer NOT NULL,
    payload_hash char(64) NOT NULL,
    applied_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, route_scope, source_entity_key),
    CONSTRAINT entity_change_state_source_not_blank CHECK (btrim(source_entity_id) <> ''),
    CONSTRAINT entity_change_state_target_not_blank CHECK (btrim(target_entity_id) <> ''),
    CONSTRAINT entity_change_state_hash_version_positive CHECK (hash_version > 0),
    CONSTRAINT entity_change_state_scope_hex CHECK (route_scope ~ '^[0-9a-f]{64}$'),
    CONSTRAINT entity_change_state_payload_hex CHECK (payload_hash ~ '^[0-9a-f]{64}$')
);
```

Add a route diagnostic index on vendor/connection/type columns. Do not add payload or run-history tables.

- [ ] **Step 4: Implement the in-memory repository**

Use `ConcurrentDictionary` keyed by normalized route fields plus lowercase source ID. Copy returned dictionaries and records; honor cancellation before every operation. Upsert must replace atomically.

- [ ] **Step 5: Implement batched PostgreSQL reads and upsert**

Call `EntitySyncDatabaseMigrator.ApplyAsync` through a one-time async initialization gate before the first query. Batch with `source_entity_key = ANY(@source_keys)` and return an ordinal-ignore-case dictionary by source ID. Upsert with:

```sql
ON CONFLICT (tenant_id, route_scope, source_entity_key)
DO UPDATE SET
    source_name = EXCLUDED.source_name,
    target_entity_id = EXCLUDED.target_entity_id,
    hash_version = EXCLUDED.hash_version,
    payload_hash = EXCLUDED.payload_hash,
    applied_at = EXCLUDED.applied_at
```

All values are parameters. No SQL includes entity names, hashes, or route values through interpolation.

- [ ] **Step 6: Run repository tests and Runtime build**

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncChangeStateRepositoryTests
dotnet build src/Runtime/LISSTech.EntitySync.Runtime.csproj --configuration Release
```

Expected: tests and build pass without warnings.

- [ ] **Step 7: Commit persistence**

```bash
git add db/migrations/002_entity_change_state.sql src/Runtime Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs
git commit -m "feat: persist entity sync change state"
```

---

### Task 3: Changed-Linked-Only Planning

**Files:**
- Modify: `src/Application/EntitySyncPlanner.cs:7-145`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs` constructor helpers only
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs` constructor helper only
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/ChangedOnlyPlanningTests.cs`

**Interfaces:**
- Consumes: `IEntityMapper`, `IEntitySyncChangeStateRepository`, `EntityWriteRequestDigest`, and Task 1 policy fields.
- Produces: `ChangedLinkedUpdatesOnly` plans containing only changed linked `Update` writes and digest-covered desired hashes.

- [ ] **Step 1: Write failing changed-only planning tests**

Use real `WeightedEntityMatcher`, `DefaultEntityMapper`, in-memory repositories, and fake adapters. Cover:

```csharp
[Fact]
public async Task FirstChangedOnlyPlanUpdatesLinkedEntityAndCarriesHash()
{
    var fixture = Fixture(source: Source("42", "Acme"), target: LinkedTarget("7", "42", "Acme"));

    var plan = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

    var item = Assert.Single(plan.Items);
    Assert.Equal("Update", item.Action);
    Assert.Equal("Linked", item.MatchType);
    Assert.Equal(EntityWriteRequestDigest.SchemaVersion, item.DesiredStateHashVersion);
    Assert.Matches("^[0-9a-f]{64}$", item.DesiredStateHash);
}

[Fact]
public async Task IdenticalCheckpointProducesUnchangedNoAction()
{
    var fixture = Fixture(source: Source("42", "Acme"), target: LinkedTarget("7", "42", "Acme"));
    var first = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);
    await fixture.ChangeStates.UpsertAsync(State(fixture.Route, first.Items[0]), default);

    var second = await fixture.Service.CreatePlanAsync(fixture.Request(EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly), default);

    var item = Assert.Single(second.Items);
    Assert.Equal("None", item.Action);
    Assert.Equal("Unchanged", item.MatchType);
}
```

Add tests for one mapped field change, target ID change, hash-version mismatch, high-confidence name-only match, ambiguous match, missing target, unmatched source, and missing/invalid `ChangeStateScope`.

- [ ] **Step 2: Run changed-only planning tests and verify red**

Expected: changed-only plans still behave like standard plans or constructor compilation fails after injecting new dependencies.

- [ ] **Step 3: Inject mapper and change-state repository**

Change the planner constructor to:

```csharp
public sealed class EntitySyncPlanner(
    IEntityConnectionRepository connections,
    IEntitySyncPlanRepository plans,
    IEntityExclusionRepository exclusions,
    IEntityMatcher matcher,
    IEntityMapper mapper,
    IEntitySyncChangeStateRepository changeStates)
```

Update every direct constructor call in tests. Use the in-memory change-state repository unless a test needs a faulting fake.

- [ ] **Step 4: Validate and batch-load changed-only state**

When policy is changed-linked-only, require a nonblank lowercase hexadecimal 64-character `ChangeStateScope`. Build `EntitySyncChangeStateRoute` from the request plus resolved connection IDs/types. Call `GetBySourceIdsAsync` once with all source IDs before finalizing items.

Store policy and scope in `EntitySyncPlanExecution`.

- [ ] **Step 5: Apply the strict planning policy before `plans.Add`**

For each normal matched item:

```csharp
if (request.UpdatePolicy == EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly)
{
    if (!item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase)
        || !item.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
        || item.Target is null)
    {
        item.Action = "None";
        item.Reasons.Add("Recurring changed-only sync permits persistently linked updates only.");
    }
    else
    {
        var write = mapper.MapUpdate(item.Source, item.Target, options);
        var hash = EntityWriteRequestDigest.Compute(write);
        item.DesiredStateHash = hash;
        item.DesiredStateHashVersion = EntityWriteRequestDigest.SchemaVersion;
        if (stored.TryGetValue(item.Source.Id, out var state)
            && state.TargetEntityId.Equals(item.Target.Id, StringComparison.OrdinalIgnoreCase)
            && state.HashVersion == EntityWriteRequestDigest.SchemaVersion
            && state.PayloadHash.Equals(hash, StringComparison.Ordinal))
        {
            item.Action = "None";
            item.MatchType = "Unchanged";
            item.Reasons.Add("Mapped update payload matches the last successful synchronization.");
        }
    }
}
```

Standard plans follow the existing branch byte-for-byte in behavior.

- [ ] **Step 6: Run planning and existing plan tests**

Run changed-only tests plus existing `CreatePlan`, exclusion, and apply-generation filters. Expected: all pass.

- [ ] **Step 7: Commit changed-only planning**

```bash
git add src/Application/EntitySyncPlanner.cs Tests/LISSTech.EntitySync.Platform.Tests
git commit -m "feat: plan changed linked updates only"
```

---

### Task 4: Successful-Write Hash Checkpointing

**Files:**
- Modify: `src/Application/EntitySyncService.cs:6-181`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs` constructor helper only
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs` constructor helper only
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/ChangedOnlyApplyTests.cs`

**Interfaces:**
- Consumes: digest-covered hashes from Task 3 and `IEntitySyncChangeStateRepository`.
- Produces: successful-write checkpoints with safe partial-failure behavior.

- [ ] **Step 1: Write failing checkpoint behavior tests**

Add tests proving:

```csharp
[Fact]
public async Task SuccessfulChangedOnlyUpdateCheckpointsDesiredHash()
{
    var fixture = await ApprovedChangedOnlyFixture(writeSuccess: true);

    var result = await fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default);
    var stored = await fixture.ChangeStates.GetBySourceIdsAsync(fixture.Route, ["42"], default);

    Assert.True(result.Success);
    Assert.Equal(fixture.Plan.Items[0].DesiredStateHash, stored["42"].PayloadHash);
    Assert.Equal("7", stored["42"].TargetEntityId);
}

[Fact]
public async Task CheckpointFailureAfterSuccessfulWriteFailsItemWithoutClaimingWriteFailure()
{
    var fixture = await ApprovedChangedOnlyFixture(writeSuccess: true, checkpointException: new InvalidOperationException("db unavailable"));

    var result = await fixture.Service.ApplyAsync("tenant", fixture.Plan.Id, true, default);

    Assert.False(result.Success);
    Assert.Equal(1, fixture.Target.UpdateCalls);
    Assert.Contains("checkpoint", Assert.Single(result.Results).Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("db unavailable", result.Results[0].Message, StringComparison.OrdinalIgnoreCase);
}
```

Also cover failed write, cancellation, standard plan, missing hash/version fail-closed, and progress counters.

- [ ] **Step 2: Run apply tests and verify red**

Expected: successful writes do not persist state and checkpoint failures cannot be injected.

- [ ] **Step 3: Inject the state repository and build the route once**

Add `IEntitySyncChangeStateRepository changeStates` to `EntitySyncService`. For changed-only plans, validate execution scope and create one route before iteration. Standard plans do not read or write change state.

- [ ] **Step 4: Checkpoint between target success and successful result recording**

After `write.Success`, require item hash/version and target ID. Upsert:

```csharp
await changeStates.UpsertAsync(new EntitySyncChangeState(
    route,
    item.Source.Id,
    item.Source.Name,
    item.Target.Id,
    item.DesiredStateHashVersion.Value,
    item.DesiredStateHash,
    TimeProvider.System.GetUtcNow()), cancellationToken).ConfigureAwait(false);
```

Use an injected `TimeProvider` rather than `TimeProvider.System` directly so tests are deterministic. A checkpoint exception records one failed item with `"Target write succeeded, but change-state checkpoint failed."`; it never appends a second result or reports raw exception text.

- [ ] **Step 5: Run apply regression tests**

Run `ChangedOnlyApplyTests`, progress callback tests, cancellation tests, and apply-once tests. Expected: all pass.

- [ ] **Step 6: Commit checkpointing**

```bash
git add src/Application/EntitySyncService.cs Tests/LISSTech.EntitySync.Platform.Tests/ChangedOnlyApplyTests.cs Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs
git commit -m "feat: checkpoint successful changed updates"
```

---

### Task 5: Shared Server Hosting Composition

**Files:**
- Create: `src/Hosting/LISSTech.EntitySync.Hosting.csproj`
- Create: `src/Hosting/IServerManagedEntityAdapterFactory.cs`
- Create: `src/Hosting/ServerManagedEntityAdapterFactory.cs`
- Create: `src/Hosting/EntitySyncHostingServiceCollectionExtensions.cs`
- Create: `src/Hosting/EntitySyncDatabaseMigrationHostedService.cs`
- Move: `mcp/AgentControllerTokenProvider.cs` to `src/Hosting/AgentControllerTokenProvider.cs`
- Move: `mcp/LogfireLogging.cs` to `src/Hosting/LogfireLogging.cs`
- Modify: `mcp/ConnectionTools.cs:1-137`
- Modify: `mcp/Program.cs:168-185`
- Modify: `mcp/LISSTech.EntitySync.Mcp.csproj:19-46`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj:16-22`
- Modify: affected test namespaces in `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs`

**Interfaces:**
- Produces: `IServerManagedEntityAdapterFactory.CreateAsync(string vendor, IReadOnlyDictionary<string,string>? profileSettings, CancellationToken)`.
- Produces: `GetNetSuiteHaloChangeStateScope()` returning a lowercase SHA-256 route scope from non-secret account identity.
- Produces: `services.AddEntitySyncPlatform(string connectionString)` shared registration and a startup migration hosted service.
- Preserves: all MCP vendors, local profile overrides, AgentController exchange behavior, and secret-safe errors.

- [ ] **Step 1: Write failing factory/composition tests**

Add tests that provide an explicit environment dictionary to the factory configuration source, verify NetSuite/Halo option construction without exposing secrets, verify stable scope across credential rotation, verify scope changes when NetSuite account or Halo base URL changes, and verify all required services resolve.

```csharp
[Fact]
public void RouteScopeIgnoresSecretRotationButChangesForAccountIdentity()
{
    var first = FactorySettings(account: "123", haloUrl: "https://halo.example.test", tokenSecret: "one");
    var rotated = FactorySettings(account: "123", haloUrl: "https://halo.example.test", tokenSecret: "two");
    var moved = FactorySettings(account: "456", haloUrl: "https://halo.example.test", tokenSecret: "two");

    Assert.Equal(first.GetNetSuiteHaloChangeStateScope(), rotated.GetNetSuiteHaloChangeStateScope());
    Assert.NotEqual(first.GetNetSuiteHaloChangeStateScope(), moved.GetNetSuiteHaloChangeStateScope());
}
```

- [ ] **Step 2: Run Hosting tests and verify red**

Expected: the Hosting project and factory do not exist.

- [ ] **Step 3: Create Hosting project and move configuration code cleanly**

Reference Core, Ports, Application, Adapters, Mapping, Matching, and Runtime; include Npgsql, Microsoft.Extensions.DependencyInjection/Hosting abstractions, and the OpenTelemetry dependencies currently owned by MCP. Move adapter option resolution, Halo access-token acquisition, AgentController provider, vendor factory branching, and Logfire configuration from MCP into Hosting. Expose the generic Logfire settings/configuration API to both executables. Do not leave aliases, duplicate helpers, or deprecated MCP paths.

The factory must accept a settings dictionary for local DPAPI profiles and otherwise read the injected environment source. Its error contract remains secret-safe.

- [ ] **Step 4: Add route scope and shared DI registration**

Canonical scope input:

```text
netsuite|<trimmed uppercase account id>|customer|netsuite|halopsa|<normalized absolute HTTPS base URL>|client|halopsa
```

Hash UTF-8 with SHA-256 lowercase hexadecimal. Do not include any key, secret, token, or OAuth scope.

Register `PostgresEntitySyncChangeStateRepository` and `TimeProvider.System` in `AddEntitySyncPlatform` alongside existing services. Register `EntitySyncDatabaseMigrationHostedService`; its `StartAsync` calls `EntitySyncDatabaseMigrator.ApplyAsync` and fails host startup on migration failure, before MCP tools or scheduler work can be served.

- [ ] **Step 5: Make MCP consume Hosting**

`ConnectionTools.ConnectVendor` retains admission/profile lookup and delegates adapter creation. `Program` calls `services.AddEntitySyncPlatform(connectionString)`, consumes the shared Logfire classes, and removes its local registration method. Update namespaces and project/package references through LSP-aware moves/refactors.

- [ ] **Step 6: Run MCP connection/security regressions**

Run platform filters covering `ConnectVendor`, AgentController exchange, environment validation, connection generations, and MCP tool reflection. Run `dotnet build mcp/LISSTech.EntitySync.Mcp.csproj -c Release`.

- [ ] **Step 7: Commit shared hosting composition**

```bash
git add src/Hosting mcp Tests/LISSTech.EntitySync.Platform.Tests
git commit -m "refactor: share server vendor composition"
```

---

### Task 6: Scheduler Run Orchestration, Locking, and Status

**Files:**
- Create: `scheduler/LISSTech.EntitySync.Scheduler.csproj`
- Create: `scheduler/EntitySyncSchedulerOptions.cs`
- Create: `scheduler/EntitySyncSchedulerStatus.cs`
- Create: `scheduler/IEntitySyncSchedulerRunLock.cs`
- Create: `scheduler/PostgresEntitySyncSchedulerRunLock.cs`
- Create: `scheduler/EntitySyncScheduledRun.cs`
- Create: `scheduler/EntitySyncSchedulerWorker.cs`
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncSchedulerTests.cs`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj`

**Interfaces:**
- Consumes: shared Hosting factory/DI, changed-only planning, checkpointing.
- Produces: `EntitySyncScheduledRun.RunAsync(CancellationToken)`, immutable status snapshots, PostgreSQL advisory lock, and immediate/12-hour `BackgroundService` loop.

- [ ] **Step 1: Write failing scheduled-run tests**

Use real application services with fake vendor adapters, fake run lock, fake `TimeProvider`, and in-memory state. Cover immediate first run, successful baseline, zero-write second run, one-source change, inactive source, overlap skip before vendor calls, prohibited action fail-closed, vendor failure, cancellation, and safe status.

```csharp
[Fact]
public async Task SuccessfulBaselineThenIdenticalRunWritesOnlyOnce()
{
    var fixture = SchedulerFixture.LinkedSource(includeInactive: true);

    var first = await fixture.Run.RunAsync(default);
    var second = await fixture.Run.RunAsync(default);

    Assert.Equal("Applied", first.State);
    Assert.Equal(1, first.Succeeded);
    Assert.Equal("Applied", second.State);
    Assert.Equal(1, second.Unchanged);
    Assert.Equal(1, fixture.Halo.UpdateCalls);
}

[Fact]
public async Task HeldRouteLockSkipsBeforeVendorConnections()
{
    var fixture = SchedulerFixture.WithUnavailableLock();

    var result = await fixture.Run.RunAsync(default);

    Assert.Equal("SkippedOverlap", result.State);
    Assert.Equal(0, fixture.Factory.CreateCalls);
}
```

- [ ] **Step 2: Run scheduler tests and verify red**

Expected: scheduler project/types do not exist.

- [ ] **Step 3: Implement immutable bounded status**

Status fields are exactly state, last start/completion, next run, plan ID, total, changed, unchanged, policy-skipped, succeeded, failed, apply-skipped, and one error capped at 512 characters. Use a lock or `Volatile` replacement for atomic immutable snapshots. Never include entity names.

- [ ] **Step 4: Implement advisory lock**

Open a dedicated Npgsql connection and execute:

```sql
SELECT pg_try_advisory_lock(hashtextextended(@route_key, 0))
```

Return `null` when false. A successful lease owns the open connection and on async dispose calls `pg_advisory_unlock` with `CancellationToken.None`, then disposes the connection. If explicit unlock fails, connection disposal still releases the session lock.

- [ ] **Step 5: Implement one scheduled run**

The run must:

1. Try the lock.
2. Create/register fresh `netsuite` and `halopsa` adapters.
3. Test both connections.
4. Build a plan with tenant `coolify-scheduler`, include inactive, create-missing false, scope from Hosting, and changed-linked-only policy.
5. Inspect all pages at 100 items while verifying a stable digest.
6. Read the repository snapshot and verify every writable item is linked update with hash/version.
7. Approve that digest.
8. Apply sequentially.
9. Return aggregate status.

Catch cancellation separately and rethrow after status records failure. Convert other exceptions to a 512-character fixed/safe summary; detailed structured logging receives exception type/message only after existing secret redaction.

- [ ] **Step 6: Implement immediate then completion-based delay**

`EntitySyncSchedulerWorker.ExecuteAsync` calls the run before its first delay. In `finally`, compute `nextRunAt = completedAt + TimeSpan.FromHours(12)` and use `Task.Delay(interval, timeProvider, stoppingToken)`. A run failure never shortens the interval or exits the host.

- [ ] **Step 7: Run scheduler tests**

Run `EntitySyncSchedulerTests` plus changed-only planning/apply tests. Expected: all pass deterministically without wall-clock sleeps.

- [ ] **Step 8: Commit scheduler core**

```bash
git add scheduler Tests/LISSTech.EntitySync.Platform.Tests
git commit -m "feat: orchestrate recurring changed-only sync"
```

---

### Task 7: Scheduler Host, Hardened Container, Compose, and Operations Docs

**Files:**
- Create: `scheduler/Program.cs`
- Create: `scheduler/Dockerfile`
- Modify: `scheduler/LISSTech.EntitySync.Scheduler.csproj`
- Modify: `docker-compose.yaml:1-91`
- Modify: `.dockerignore`
- Modify: `justfile:327-360`
- Modify: `mcp/README.md:20-117`
- Modify: `README.md:387-415`
- Modify: `.env.example`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs` tool/build reflection only

**Interfaces:**
- Consumes: Task 6 worker/status and Task 5 Hosting DI.
- Produces: HTTP `/health`, `/status`, `entitysync-scheduler` image/service, `just scheduler-build`, and valid Coolify Compose configuration.

- [ ] **Step 1: Write failing host/Compose contract tests**

Add tests for status JSON field allowlist, health remaining healthy after failed run, scheduler assembly entry point, and DI startup validation. Avoid source-text assertions for behavior; Compose validation remains a smoke command.

- [ ] **Step 2: Implement scheduler host**

Use `WebApplication.CreateBuilder`, console logging, the same OTLP log exporter configuration as MCP with default `OTEL_SERVICE_NAME=lisstech-entitysync-scheduler`, shared platform registration, scheduler lock/run/status/worker singletons, and startup migration application.

Map:

```csharp
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/status", (EntitySyncSchedulerStatus status) => Results.Ok(status.Snapshot));
```

No authentication is required because Compose exposes the port internally only. Do not publish it to the host.

- [ ] **Step 3: Add hardened scheduler Dockerfile**

Use the same digest-pinned SDK/runtime images as MCP. Copy only project files required for restore, publish framework-dependent output, set `ASPNETCORE_HTTP_PORTS=8080`, run as `$APP_UID`, and use `ENTRYPOINT ["dotnet", "lisstech-entitysync-scheduler.dll"]`.

- [ ] **Step 4: Add Compose sidecar**

Add `entitysync-scheduler` with:

```yaml
entitysync-scheduler:
  build:
    context: .
    dockerfile: scheduler/Dockerfile
  depends_on:
    entitysync-db:
      condition: service_healthy
  environment:
    DATABASE_URL: "${DATABASE_URL:?DATABASE_URL must be set for durable state}"
    OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: "${OTEL_EXPORTER_OTLP_LOGS_ENDPOINT:?OTEL logs endpoint is required}"
    OTEL_EXPORTER_OTLP_HEADERS: "${OTEL_EXPORTER_OTLP_HEADERS:?OTEL headers are required}"
    OTEL_EXPORTER_OTLP_PROTOCOL: "${OTEL_EXPORTER_OTLP_PROTOCOL:-http/protobuf}"
    OTEL_SERVICE_NAME: "${SCHEDULER_OTEL_SERVICE_NAME:-lisstech-entitysync-scheduler}"
    HALO_BASE_URL: "${HALO_BASE_URL:?HALO_BASE_URL is required by scheduler}"
    HALO_CLIENT_ID: "${HALO_CLIENT_ID:?HALO_CLIENT_ID is required by scheduler}"
    HALO_CLIENT_SECRET: "${HALO_CLIENT_SECRET:?HALO_CLIENT_SECRET is required by scheduler}"
    HALO_ACCOUNT_MANAGER_EMAIL: "${HALO_ACCOUNT_MANAGER_EMAIL:-}"
    HALO_NETSUITE_CUSTOMER_ID_FIELD_ID: "${HALO_NETSUITE_CUSTOMER_ID_FIELD_ID:-}"
    HALO_NETSUITE_CUSTOMER_NAME_FIELD: "${HALO_NETSUITE_CUSTOMER_NAME_FIELD:-}"
    NETSUITE_ACCOUNT_ID: "${NETSUITE_ACCOUNT_ID:?NETSUITE_ACCOUNT_ID is required by scheduler}"
    NETSUITE_CONSUMER_KEY: "${NETSUITE_CONSUMER_KEY:?NETSUITE_CONSUMER_KEY is required by scheduler}"
    NETSUITE_CONSUMER_SECRET: "${NETSUITE_CONSUMER_SECRET:?NETSUITE_CONSUMER_SECRET is required by scheduler}"
    NETSUITE_TOKEN_ID: "${NETSUITE_TOKEN_ID:?NETSUITE_TOKEN_ID is required by scheduler}"
    NETSUITE_TOKEN_SECRET: "${NETSUITE_TOKEN_SECRET:?NETSUITE_TOKEN_SECRET is required by scheduler}"
```

Add internal expose/healthcheck and copy MCP hardening: `init`, `read_only`, 64 MiB `/tmp`, `no-new-privileges`, `cap_drop: ALL`, `restart: unless-stopped`.

- [ ] **Step 5: Add build recipes and fix cross-platform Compose path**

Add `scheduler-build` and `scheduler-run` recipes. Change existing Compose recipe paths from backslash concatenation to `{{ project_root }}/docker-compose.yaml`, then add `scheduler-docker-build` or make `mcp-docker-build` build both application images.

- [ ] **Step 6: Document Coolify configuration and behavior**

Document immediate baseline reconciliation, active+inactive scope, linked-only writes, 12-hour post-completion interval, PostgreSQL state, no immediate retries, route locking, health/status, mapper version reconciliation, and the fact that manual Halo drift is not detected. Update `.env.example` with `SCHEDULER_OTEL_SERVICE_NAME` only; reuse existing vendor/database variables.

- [ ] **Step 7: Run host/build/Compose verification**

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter "FullyQualifiedName~EntitySyncSchedulerTests|FullyQualifiedName~ChangedOnly"
dotnet publish scheduler/LISSTech.EntitySync.Scheduler.csproj -c Release -r osx-arm64 -p:SelfContained=true -o Build/Scheduler
docker compose --file docker-compose.yaml config --quiet
docker compose --file docker-compose.yaml build entitysync-mcp entitysync-scheduler
```

Supply validation-only required environment variables for Compose commands. Expected: every command exits 0 and both images run as non-root read-only services.

- [ ] **Step 8: Commit host and deployment**

```bash
git add scheduler docker-compose.yaml .dockerignore justfile mcp/README.md README.md .env.example Tests/LISSTech.EntitySync.Platform.Tests
git commit -m "feat: deploy changed-only sync sidecar"
```

---

### Task 8: Full Verification and Coolify Deployment

**Files:**
- Verify only; source changes only if a verification failure exposes a root-cause defect.

**Interfaces:**
- Consumes: complete sidecar, migration, shared Hosting, and Compose deployment.
- Produces: verified main revision, deployed Coolify sidecar, successful baseline, and zero-write unchanged second run.

- [ ] **Step 1: Run repository verification**

```bash
just build
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release
just mcp-build
just scheduler-build
just mcp-compose-config
```

Expected: build and all platform tests pass with zero warnings/errors. If the existing macOS-only Pester DPAPI baseline still fails or hangs, record that exact unrelated limitation and do not substitute it for the platform suite.

- [ ] **Step 2: Verify migrations and lock against PostgreSQL**

Start the Compose PostgreSQL service with validation credentials. Run `EntitySyncDatabaseMigrator.ApplyAsync`, verify migrations `001_entity_exclusions` and `002_entity_change_state` are recorded, exercise one upsert/batch read, and acquire/deny/release the scheduler advisory lock from two connections. Do not print database credentials or state payloads.

- [ ] **Step 3: Build images and smoke the scheduler without vendor writes**

```bash
docker compose --file docker-compose.yaml build entitysync-mcp entitysync-scheduler
docker compose --file docker-compose.yaml run --rm --no-deps --entrypoint dotnet entitysync-mcp --info
docker compose --file docker-compose.yaml run --rm --no-deps --entrypoint dotnet entitysync-scheduler --info
```

Start PostgreSQL and the scheduler with syntactically valid but deliberately non-routable HaloPSA credentials and invalid test-only NetSuite credentials. Verify scheduler `/health` remains healthy, `/status` becomes `Failed` after the connection check, no plan/write occurs, and status contains only approved aggregate fields. Stop that scheduler before restoring real environment values. Never start the scheduler with production vendor credentials before Step 4 completes.

- [ ] **Step 4: Review the initial production-impact plan before push**

Using a one-off review harness, invoke the same Hosting factory, planning request, repository checks, and full-page validation used by `EntitySyncScheduledRun`, but stop before approval/apply. Confirm all writable rows are `Update + Linked`, all other rows are `None`, and no `Create`, `Link`, or `Review` action can reach apply. Record plan ID, digest, and aggregate counts only; do not emit entity names. Delete the review-only harness before committing or deploying.

- [ ] **Step 5: Push and deploy**

Fast-forward the reviewed feature branch to `main`, push `origin/main`, and allow the configured Coolify Git deployment to rebuild the Compose resource. Confirm the deployed MCP tool behavior remains available and the scheduler container reports healthy.

- [ ] **Step 6: Verify production baseline and suppression**

Observe the immediate scheduler run to terminal status. Confirm successful linked writes checkpoint state and failed rows remain uncheckpointed. Trigger one controlled second run through a restart only after the first is terminal; changed-only hashes must produce zero HaloPSA writes for unchanged successful rows. Confirm `/status` reports the next run approximately 12 hours after completion.

- [ ] **Step 7: Verify one changed entity**

Use a controlled NetSuite customer change already authorized by the operator, or a deterministic staging route when production mutation is not authorized. The next run must show exactly one changed linked update, checkpoint its new hash, and leave every other successful baseline row unchanged. Restore staging data when applicable; never mutate production solely to manufacture a test.

- [ ] **Step 8: Confirm revision and cleanup**

Confirm local `HEAD`, `origin/main`, and deployed revision match. Remove the owned feature worktree/branch after merged-result tests pass. Report commit, build/test counts, baseline changed/unchanged/failure counts, next-run timestamp, and any externally blocked production verification exactly.
