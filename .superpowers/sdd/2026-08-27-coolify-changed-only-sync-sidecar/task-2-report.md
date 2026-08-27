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
