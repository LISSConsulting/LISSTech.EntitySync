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
- The idempotent executor takes a transaction-scoped advisory lock derived from tenant plus key, compares normalized SHA-256 hashes under row lock, replays only a complete safe JSON response, and writes the response in the same transaction. Command exceptions roll back without a receipt.

## Self-Review

- All methods from the seven Task 2 repository interfaces have exactly one production implementation.
- Migration 004-006 columns are mapped explicitly; plan policy hashes and operation/inspection connection IDs are restored through tenant-qualified joins because the final schema normalizes those values into referenced rows.
- Latest-policy filtering occurs after selecting the latest version, preventing an older enabled version from being returned when the latest version is disabled.
- No production repository uses process-local state, synchronous database calls, plaintext secret/full-value columns, interpolated SQL values, direct controller database writes, compatibility aliases, or suppressed exceptions.
- Focused output is warning-free.

## Concerns

None.
