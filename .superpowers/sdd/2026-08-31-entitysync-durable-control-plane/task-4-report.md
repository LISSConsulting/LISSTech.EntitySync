# Task 4 Implementation Report

## Status

DONE

## Commit

`feat(control): version connections and sync policies`

The containing commit SHA is returned in the Task 4 terminal result. A Git commit cannot embed its own object ID in content that participates in that object ID.

## Changed Files

- `src/Application/ConnectionDefinitionService.cs`
- `src/Application/SyncPolicyService.cs`
- `src/Application/EntitySyncPlanner.cs`
- `src/Application/EntitySyncService.cs`
- `src/Application/EntityExclusionService.cs`
- `src/Hosting/ConnectionRuntimeFactory.cs`
- `src/Hosting/IServerManagedEntityAdapterFactory.cs`
- `src/Hosting/ServerManagedEntityAdapterFactory.cs`
- `src/Hosting/EntitySyncHostingServiceCollectionExtensions.cs`
- `src/Ports/IConnectionDefinitionRepository.cs`
- `src/Ports/IEntityAdapter.cs`
- `src/Ports/IEntityConnectionRepository.cs`
- `src/Ports/ISyncPolicyRepository.cs`
- `src/Runtime/PostgresConnectionDefinitionRepository.cs`
- `src/Runtime/PostgresSyncPolicyRepository.cs`
- `src/Runtime/ConnectionRegistry.cs`
- `src/Runtime/InMemoryEntityConnectionRepository.cs`
- `mcp/ConnectionTools.cs`
- `scheduler/EntitySyncScheduledRun.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ConnectionDefinitionServiceTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/SyncPolicyServiceTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncSchedulerTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-4-report.md`

The existing test files changed only to follow the clean-cutover runtime/factory contracts and to keep their local test doubles explicit.

## RED Evidence

Exact command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter "FullyQualifiedName~ConnectionDefinitionServiceTests|FullyQualifiedName~SyncPolicyServiceTests"
```

The initial run failed at compile time for the intentionally absent Task 4 types, including:

```text
error CS0246: The type or namespace name 'ConnectionDefinitionService' could not be found
error CS0246: The type or namespace name 'SyncPolicyService' could not be found
error CS0246: The type or namespace name 'IConnectionRuntimeFactory' could not be found
error CS0246: The type or namespace name 'EntityAdapterCapabilities' could not be found
```

A later regression RED proved that credential rotation incorrectly re-enabled a disabled connection:

```text
ConnectionDefinitionServiceTests.Updating_a_disabled_connection_does_not_reenable_it
Assert.False() Failure
Expected: False
Actual:   True
Failed: 1, Passed: 21, Total: 22
```

## GREEN Evidence

The same exact focused command passed after the final fix:

```text
Passed! - Failed: 0, Passed: 24, Skipped: 0, Total: 24, Duration: 315 ms
```

No formatter, linter, Pester, or project-wide suite was run.

## Decisions

- Connection requests are strictly tenant-scoped and validate known vendors, stable connection IDs, required display names, defined JSON values, nonempty secret values, unique case-insensitive keys, and disjoint public/secret key sets.
- Secrets are canonically serialized, protected before repository insertion, never copied into public configuration or returned as plaintext, and cleared from avoidable mutable dictionaries after protection or adapter construction.
- Create starts at generation 1. Credential/configuration replacement, disable, and delete fallback use exact generation compare-and-swap. Updating a disabled connection preserves its disabled state.
- Repository deletion locks the exact tenant/connection generation. It deletes only when no immutable policy version or durable plan references the connection; a referenced definition is deterministically disabled through the same expected generation.
- `ConnectionRuntimeFactory` loads one exact durable definition per acquisition, rejects missing/disabled/stale definitions, decrypts only for adapter creation, rechecks the generation after construction, and owns/disposes one adapter per lease. It has no tenant- or generation-crossing adapter/secret cache.
- Durable adapter construction merges persisted public and secret configuration only inside construction and uses an empty environment source. Remote MCP connect snapshots the existing server-managed configuration into encrypted durable definitions; later acquisitions cannot silently fall back to changed process environment credentials. AgentController has an explicit persisted service-principal construction path.
- Adapter capabilities are obtained through `IEntityAdapter.GetCapabilitiesAsync`. Policy validation requires readable source and updatable target entities, create support when requested, exact supported external/custom/allowed/blocked fields, and adapter-declared scheduled-safe fields. Default scheduled-safe capability is fail-closed.
- Production policy topology is hub-and-spoke: exactly one endpoint must be `OrchestraMSP`. Both connection definitions must be current, enabled, tenant-owned, vendor-matching, and generation-pinned.
- Capability validation acquires both exact runtime leases, rechecks both definitions afterward, and inserts through a PostgreSQL transaction that holds ordered `FOR SHARE` locks on both exact connection rows. Concurrent rotation cannot commit a policy validated against an older generation.
- Every policy change inserts an immutable version row. The Core canonical serializer fixes property order and field-set order for SHA-256; PostgreSQL storage also sorts allowed/blocked field arrays deterministically.
- `IEntityConnectionRepository` resolves only for explicit `LocalStdio` registration, where the same in-memory singleton implements `IConnectionRuntimeFactory`. HTTP and scheduler use the PostgreSQL definition repository plus `ConnectionRuntimeFactory`; planner, apply, MCP get/test/connect, and scheduler acquisition callsites use async generation-pinned leases. Exclusion route metadata and connection listing use mode-aware metadata paths, avoiding adapter construction/network authentication while preserving LocalStdio visibility. The actual scheduler host dependency graph is exercised by the focused tests.

## Self-Review

- Tenant predicates are present on every new PostgreSQL read, lock, reference check, delete, and insert path.
- Connection update/delete and policy insertion are fenced against concurrent generations; policy version duplicates return a deterministic version conflict rather than mutating an old row.
- Runtime adapters are disposed on normal lease release, adapter/vendor mismatch, missing-after-create, disable, or generation rotation during construction.
- Plaintext is absent from durable models, returned connection definitions, SQL parameters, logs, exception messages, and process-wide state.
- Capability validation is case-insensitive where vendor/entity/field contracts are case-insensitive, but connection identity remains exact and tenant-qualified.
- HTTP/scheduler service registration contains no `InMemoryEntityConnectionRepository` fallback and no generation-agnostic runtime adapter cache.
- `git diff --check` passed before final verification.

## Concerns

- The first-class `OrchestraMSP` adapter is intentionally introduced by Task 9. Until that task lands, production policy creation involving OrchestraMSP fails closed at runtime adapter construction; the Task 4 service and capability contract are covered with generation-pinned adapter fakes.

## Fix Round 1

Status: DONE

- Migration `007_connection_generation_ledger.sql` adds a permanent tenant/connection generation ledger, backfills existing definitions, and rejects counter deletion or regression. Repository create and generation-CAS replacement allocate and persist the ledger generation in the same transaction as the definition row, so physical delete/recreate cannot produce ABA generation reuse.
- Durable plan insertion now takes deterministic `FOR SHARE` locks on both exact enabled connection generations before inserting the plan and items in the same transaction. Connection deletion and plan insertion therefore serialize with either a referenced live connection or a rejected stale plan, never an orphan.
- MCP `ConnectVendor` now keeps normalization, mode dispatch, dependency/config/profile resolution, and connection mutation inside its cancellation-preserving error boundary. Remote configuration secrets are cleared in `finally`; invalid vendors, missing remote configuration, and invalid LocalStdio profiles return structured safe errors.
- Focused service and MCP command:

```text
Passed! - Failed: 0, Passed: 27, Skipped: 0, Total: 27, Duration: 288 ms
```

- The first PostgreSQL test attempt stopped in database initialization because no `DATABASE_URL` was configured. Fix Round 2 records the later successful execution with the provided local development connection string.

## Fix Round 2

Status: DONE

- `ServerManagedEntityAdapterFactory.GetConnectionConfiguration` now owns its mutable secret buffer and clears both partially built secret and public configuration on every exceptional exit before rethrowing. `ConnectionTools.ConnectVendor` retains its `finally` cleanup for successfully returned configuration.
- RED: the exact partial NetSuite secret test initially failed to compile because the observable secret-buffer constructor did not exist.
- GREEN:

```text
Task 4 service filter: Passed 26, Failed 0, Skipped 0, Duration 299 ms
Partial NetSuite MCP test: Passed 1, Failed 0, Skipped 0, Duration < 1 ms
PostgreSQL ABA/plan-delete tests: Passed 2, Failed 0, Skipped 0, Duration 191 ms
```

- The PostgreSQL tests used the provided local development `DATABASE_URL`; the compose database was already running, so no service was started.
