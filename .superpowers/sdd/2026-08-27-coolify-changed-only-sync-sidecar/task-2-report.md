# Task 2 Report: Change-State Persistence

## Status

Complete. Every Task 2 brief checkbox is implemented. No planner, apply, hosting, scheduler, or deployment files were changed.

## Changed Files

- `db/migrations/002_entity_change_state.sql`
- `src/Runtime/InMemoryEntitySyncChangeStateRepository.cs`
- `src/Runtime/PostgresEntitySyncChangeStateRepository.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs`
- `.superpowers/sdd/2026-08-27-coolify-changed-only-sync-sidecar/task-2-report.md` (this report)

## Design Decisions

- The in-memory repository uses a `ConcurrentDictionary` whose key contains every Task 1 route field and a lowercase invariant source entity ID. `AddOrUpdate` performs atomic replacement.
- In-memory writes and reads copy both `EntitySyncChangeState` and its nested route record. Each batch read returns a new ordinal-ignore-case dictionary.
- PostgreSQL reads use one `source_entity_key = ANY(@source_keys)` query, with deduplicated lowercase keys, the complete route predicate, and an ordinal-ignore-case result dictionary.
- PostgreSQL upsert uses the required `(tenant_id, route_scope, source_entity_key)` conflict target and updates only the mutable state fields listed in the brief.
- A per-repository `SemaphoreSlim` plus a volatile completion flag gates `EntitySyncDatabaseMigrator.ApplyAsync`. Failed or cancelled initialization does not mark the gate complete, so a later operation can retry safely; concurrent first operations share the completed initialization.
- Every state, route, hash, entity name, and ID is passed through an Npgsql parameter. SQL composition is limited to constant SQL fragments.
- Migration `002` creates only `entitysync.entity_change_state` and its route diagnostic index. It relies on the existing embedded-resource wildcard and migrator advisory lock/transaction behavior.

## TDD Evidence

### Red

The focused tests were written before either repository implementation existed.

Command:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncChangeStateRepositoryTests
```

Exit code: `1`

Output proving the intended missing-implementation failure:

```text
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs(15,30): error CS0246: The type or namespace name 'InMemoryEntitySyncChangeStateRepository' could not be found (are you missing a using directive or an assembly reference?) [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs(31,30): error CS0246: The type or namespace name 'InMemoryEntitySyncChangeStateRepository' could not be found (are you missing a using directive or an assembly reference?) [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs(47,30): error CS0246: The type or namespace name 'InMemoryEntitySyncChangeStateRepository' could not be found (are you missing a using directive or an assembly reference?) [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs(64,30): error CS0246: The type or namespace name 'InMemoryEntitySyncChangeStateRepository' could not be found (are you missing a using directive or an assembly reference?) [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncChangeStateRepositoryTests.cs(85,30): error CS0246: The type or namespace name 'InMemoryEntitySyncChangeStateRepository' could not be found (are you missing a using directive or an assembly reference?) [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
```

### Green

The tests cover route isolation and replacement, case-insensitive source identity, batched reads, defensive dictionary/record snapshots, and cancellation without mutation.

Final command:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncChangeStateRepositoryTests
```

Exit code: `0`

Final output:

```text
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 15 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

## Focused Build Evidence

Command:

```bash
dotnet build src/Runtime/LISSTech.EntitySync.Runtime.csproj --configuration Release
```

Exit code: `0`

Output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  LISSTech.EntitySync.Core -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Core/bin/Release/net8.0/LISSTech.EntitySync.Core.dll
  LISSTech.EntitySync.Ports -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Ports/bin/Release/net8.0/LISSTech.EntitySync.Ports.dll
  LISSTech.EntitySync.Runtime -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Runtime/bin/Release/net8.0/LISSTech.EntitySync.Runtime.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:00.90
```

No project-wide validation was run.

## Self-Review

- Re-read the Task 2 brief and checked every implementation checkbox against the final files.
- Confirmed the migration has the required primary key, checks, and diagnostic index, and adds no payload/run-history tables.
- Confirmed in-memory route/source isolation, atomic replacement, case-insensitive identity, cancellation checks, and defensive snapshots.
- Confirmed PostgreSQL performs one batched read, includes the full route predicate, uses the required atomic upsert, and routes all values through parameters.
- Confirmed initialization is one-time after success, serializes concurrent initialization, and remains retryable after cancellation/failure.
- Confirmed only Task 2 implementation/test files and this requested report changed.
- Independent code review of commit `84d14c95e7a638811251df17449b7e95aec717e5` reported no Critical, Important, or Minor findings and assessed the commit as correct and ready.

## Commits

- `84d14c95e7a638811251df17449b7e95aec717e5` — `feat: persist entity sync change state`

## Concerns

No identified correctness concern. The prescribed focused tests exercise the in-memory repository contract; the PostgreSQL implementation and migration were compiled and source-reviewed, but no live PostgreSQL instance was provisioned or exercised by the Task 2 brief.

## Fix Round 1

### Decisions

- Hardened the database identity instead of retaining the brief's narrow primary-key example. The primary key and PostgreSQL `ON CONFLICT` target now contain, in the same order, tenant, scope, the complete source vendor/connection/type triple, the complete target triple, and normalized source key.
- Centralized source-key normalization behind one invariant .NET helper used by in-memory reads/writes and PostgreSQL reads/writes. PostgreSQL no longer calls `lower()` and binds `@source_entity_key` to the already normalized value.
- Set `source_entity_key`, `source_entity_id`, `source_name`, and `target_entity_id` to `varchar(512)`, matching the established 512-character exclusion/entity conventions. Both repositories validate and trim those state values before mutation; reads validate source IDs through the same source-key normalizer.
- No live PostgreSQL test infrastructure exists in the current test project, so no service or Testcontainers dependency was added. Deterministic coverage instead reads the embedded migration and constructs the repository's actual Npgsql upsert command without opening a connection. It verifies the complete primary/conflict identities, bounded schema columns, normalized bound parameter, and absence of PostgreSQL `lower()`.
- Route-isolation regression coverage holds scope and source ID fixed while independently varying tenant and each field in both vendor/connection/type triples.

### TDD Evidence

The new regression tests were run before the fixes. They failed for the missing limits, missing command-construction path, and incomplete migration schema:

```text
Failed!  - Failed:     5, Passed:     6, Skipped:     0, Total:    11, Duration: 65 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

### Exact Verification

Command:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncChangeStateRepositoryTests
```

Exit code: `0`

Output:

```text
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 55 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

Command:

```bash
dotnet build src/Runtime/LISSTech.EntitySync.Runtime.csproj --configuration Release
```

Exit code: `0`

Output:

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  LISSTech.EntitySync.Core -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Core/bin/Release/net8.0/LISSTech.EntitySync.Core.dll
  LISSTech.EntitySync.Ports -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Ports/bin/Release/net8.0/LISSTech.EntitySync.Ports.dll
  LISSTech.EntitySync.Runtime -> /Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/src/Runtime/bin/Release/net8.0/LISSTech.EntitySync.Runtime.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.20
```

No broad validation was run.

### Fix-Round Self-Review

- Confirmed the migration primary key and the upsert conflict target have identical complete route/source identity fields and order.
- Confirmed the route-isolation regression fixes scope and source ID while varying tenant and every source/target triple field one at a time.
- Confirmed there is only one invariant lowercase operation for persistence source keys and that PostgreSQL binds its output as `@source_entity_key`.
- Confirmed all four state columns use 512-character schema limits and that exact-boundary and 513-character rejection tests exercise both repositories without a database connection.
- Confirmed cancellation remains checked before validation or state/database access.
- Confirmed no Task 1 contract, unrelated task, planner, apply, hosting, scheduler, deployment, or project configuration file was changed.

### Fix-Round Concern

No live PostgreSQL server was available in existing test infrastructure. The permitted deterministic embedded-migration and actual-command coverage was used instead; no Testcontainers dependency was introduced.
