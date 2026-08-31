# Task 3 Implementation Report

## Status

DONE

## Commit

`feat(control): persist and encrypt control state`

The containing commit SHA is returned in the Task 3 terminal result. A Git commit cannot embed its own object ID in content that participates in that object ID.

## Changed Files

- `src/Ports/IEntitySyncDataProtector.cs`
- `src/Runtime/EntitySyncDataProtector.cs`
- `src/Runtime/PostgresControlPersistence.cs`
- `src/Runtime/PostgresConnectionDefinitionRepository.cs`
- `src/Runtime/PostgresSyncPolicyRepository.cs`
- `src/Runtime/PostgresDurableSyncPlanRepository.cs`
- `src/Runtime/PostgresSyncOperationRepository.cs`
- `src/Runtime/PostgresSyncScheduleRepository.cs`
- `src/Runtime/PostgresSyncAuditRepository.cs`
- `src/Runtime/PostgresIdempotencyRepository.cs`
- `src/Runtime/LISSTech.EntitySync.Runtime.csproj`
- `src/Hosting/EntitySyncHostingServiceCollectionExtensions.cs`
- `mcp/Program.cs`
- `scheduler/Program.cs`
- `mcp/Dockerfile`
- `scheduler/Dockerfile`
- `docker-compose.yaml`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlRepositoryTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncSchedulerHostTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-3-report.md`

## RED Evidence

Command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~ControlRepositoryTests
```

The initial focused run failed at compile time for the intentionally absent production abstraction:

```text
ControlRepositoryTests.cs(404,20): error CS0246: The type or namespace name 'IEntitySyncDataProtector' could not be found
```

## GREEN Evidence

The same exact focused command passed against PostgreSQL 18.3 (new database per test, migrations 001-006 applied):

```text
Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9, Duration: 2 s
```

The focused tests cover tenant isolation; external-key-ring, application-name, and purpose isolation; ciphertext non-disclosure; connection/policy round trips and CAS; sealed manifest round trip and transaction rollback; exact inspection coverage; approval races, expiry, and single-use consumption; lease races, expired-lease reclamation, and stale worker item fencing; schedule/change-event CAS; audit ciphertext retention; plan expiration; idempotency replay, conflict, concurrency, tenant isolation, and rollback; and Hosting resolution.

No formatter, linter, Pester, or project-wide test suite was run.

## Security and Atomicity Decisions

- Every repository method is tenant-first. Every production read, update, delete, join, conflict target, page, and lock includes the tenant predicate, and model ownership is rejected before writes.
- All SQL value inputs use typed `NpgsqlParameter` values. Repository I/O is asynchronous, cancellation-aware, and uses `await using` for commands, readers, connections, transactions, and binary resources.
- Data Protection uses the fixed application name `LISSTech.EntitySync.Control`, externally persisted keys, and distinct versioned purposes `connection-secret-v1` and `audit-value-v1`. HTTP and scheduler registration fails closed without `ENTITYSYNC_DATA_PROTECTION_KEY_PATH`; explicit local stdio mode alone may use the user-local application-data path.
- The HTTP and scheduler containers share a writable named key-ring volume while retaining read-only root filesystems and non-root execution.
- Durable plan insertion accepts only `EntitySyncDurablePlanManifest` and inserts the plan and every ordered item in one transaction. Any policy mismatch, item constraint failure, or cancellation rolls back the plan.
- Inspection completion relies on the migration's exact contiguous-coverage trigger and adds digest, generation, connection-ID, status, and tenant CAS predicates. Approval insertion and Draft-to-Approved transition share one transaction.
- Approval consumption locks/transitions the exact unexpired Approved plan, verifies the exact manifest item count, inserts the full Apply operation graph, and commits once. The plan CAS and unique Apply-approval index make consumption single-use under races.
- Operation leasing uses `FOR UPDATE SKIP LOCKED`, reclaims expired Leased or Running operations, increments attempts, replaces owner/expiry, and clears stale start/completion state. Operation state replacement fences the attempt and live lease; item CAS additionally fences tenant, operation, plan, item, attempt, owner, expiry, and expected outcome.
- Bounded snapshot, audit full-value, and idempotency retention use ordered `FOR UPDATE SKIP LOCKED` deletion batches. Audit event plus encrypted full values are inserted transactionally.
- The idempotent executor uses a session advisory lock across a durable two-transaction protocol: it commits an incomplete tenant/key/hash claim before invoking the downstream command with a stable idempotency context token, then atomically stores the safe response. Crashes retain the claim; same-hash retries reuse the token; different hashes conflict before invocation.

## Self-Review

- All methods from the seven Task 2 repository interfaces have exactly one production implementation.
- Migration 004-006 columns are mapped explicitly; plan policy hashes and operation/inspection connection IDs are restored through tenant-qualified joins because the final schema normalizes those values into referenced rows.
- Latest-policy filtering occurs after selecting the latest version, preventing an older enabled version from being returned when the latest version is disabled.
- No production repository uses process-local state, synchronous database calls, plaintext secret/full-value columns, interpolated SQL values, direct controller database writes, compatibility aliases, or suppressed exceptions.
- Focused output is warning-free.

## Concerns

None.

## Fix Round 1

### Status

DONE

### Parent Commit

`f143e0a505f605f40ebacd8c57389e7a3a518c1d` — `feat(control): persist and encrypt control state`

The fix commit SHA is returned in the Task 3 fix-round terminal result because a commit cannot embed its own object ID.

### RED

The exact focused command failed at compile time after adding the durable crash/restart contract:

```text
ControlRepositoryTests.cs: error CS0246: The type or namespace name
'IdempotencyExecutionContext' could not be found
```

The new behavioral tests also target live connection-generation races, illegal operation transitions, pending-item terminal rejection, Unix key-ring permissions, a 2,500-item manifest, snapshot retention, direct idempotency methods, pending canonical changes, and audit pagination.

### GREEN

Exact repository command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~ControlRepositoryTests
```

Result:

```text
Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14, Duration: 3 s
```

Individually filtered scheduler-host validation:

```text
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: < 1 ms
```

No broad suite, formatter, linter, or Pester run was performed.

### Fixes

- The executor now holds a session advisory lock across a two-transaction protocol. Transaction one persists and commits an incomplete claim. The downstream callback receives `IdempotencyExecutionContext` with tenant, key, and a deterministic tenant/key/hash token. Transaction two stores the response. A simulated post-effect crash proves the claim survives and the idempotent fake downstream observes one logical effect across restart; a different hash never invokes the callback.
- Inspection open, range insertion, completion, approval, and consumption revalidate both current tenant-scoped connection IDs and generations. Rotation after planning now fails before range/session completion, approval, plan-status, or operation mutation.
- New operation graphs require a fresh Queued attempt-zero operation and fresh Pending items. Replacement locks and compares immutable identity, admits only legal lifecycle edges, fences live leases and attempts, and atomically checks Succeeded/Partial/Failed item outcome consistency. Item outcomes allow one Pending-to-terminal transition.
- Production key directories must already exist for HTTP/scheduler, be writable by the process, and use Unix owner-only 0700 permissions. Container directories are created as the non-root application owner's 0700 directory. Focused protection proves generated key XML has no group/other permissions.
- Plan items use typed Npgsql binary `COPY` inside the plan transaction. The 2,500-item focused test round-trips the final page and total count; the existing malformed-JSON test still proves the COPY failure rolls back the plan.
- Focused SQL tests now exercise direct idempotency claim/complete/delete behavior, snapshot get/delete retention, operation terminal variants, pending canonical-change listing/CAS, audit continuation pagination/full-value deletion, and crash/restart behavior.

### Concerns

None.

## Fix Round 2

### Status

DONE

### RED

The exact focused command failed against the pre-fix behavior:

```text
Failed: 3, Passed: 13, Skipped: 0, Total: 16
```

- Concurrent connection rotation did not block inspection/consume mutations.
- Concurrent reclaim/cancel did not block operation-item mutation.
- Mixed succeeded/failed items incorrectly allowed a `Failed` operation.

### GREEN

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~ControlRepositoryTests
Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16, Duration: 4 s
```

No broad validation was run.

### Fixes

- Inspection open, range insertion, completion, approval, and approval consumption now run while holding tenant-scoped `FOR SHARE` locks on both current connection-definition rows. The single lock query orders by connection ID, validates both exact generations after lock acquisition, and keeps the locks through mutation commit. Concurrent rotation tests hold an update lock first, prove the stale mutation waits, then prove completion or consumption fails without changing inspection, plan, or operation state.
- Operation-item CAS now opens a transaction, locks the tenant/operation/plan row `FOR UPDATE`, and re-reads attempt, owner, mutable status, and lease liveness using PostgreSQL `now()`. Only a valid locked operation may update a Pending item before the shared transaction commits. Concurrent reclaim and cancel transactions prove the item mutation waits and then returns false without changing the Pending item.
- A `Failed` operation now requires at least one failed item and rejects every succeeded, skipped, pending, or unknown item. Mixed successful/skipped and failed outcomes are exclusively `Partial`; focused tests cover accepted all-failed, accepted mixed-partial, and rejected mixed-failed transitions.

### Concerns

None.
