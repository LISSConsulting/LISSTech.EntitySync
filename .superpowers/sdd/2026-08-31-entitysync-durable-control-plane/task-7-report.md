# Task 7 — Durable Policy Schedules

## RED

- Focused command: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter "FullyQualifiedName~ControlSchedulerTests|FullyQualifiedName~CanonicalChangeServiceTests"`
- Initial RED failed to compile because `CanonicalChangeRequest`, receipt/repository/work-signal/version-reader contracts, `SyncScheduleService`, durable work queue, control worker, and retention behavior did not exist (`artifact://1434`).
- The expanded concurrency/fencing RED failed on missing stable schedule-work identity, due gating, and route-lease expiry helpers (`artifact://1442`).

## GREEN

- Exact focused filter passed: 15 passed, 0 failed, 0 skipped, 98 ms (`artifact://1455`).
- Minimal touched runtime build passed: `dotnet build scheduler/LISSTech.EntitySync.Scheduler.csproj --configuration Release` (`artifact://1453`).
- No formatter, linter, Pester, broad build, or project-wide test suite was run.

## Implementation

- Added Cronos 0.11.1 and immutable, versioned schedule create/edit/disable behavior with standard five-field cron parsing, validated `TimeZoneInfo`, and deterministic DST next-run computation.
- Added PostgreSQL migration 012 for atomic due schedule claim/advance, canonical intake receipts, durable control work, route leases, notification triggers, and metadata-preserving audit/operation ciphertext redaction.
- Added canonical-change intake with tenant + OM event replay identity, canonical UUID/version/hash conflict detection, atomic receipt/work linkage, generation-pinned exact-version adapter validation, and durable wakeup.
- Added `PostgresSyncWorkQueue`, transaction-scoped PostgreSQL advisory route acquisition plus fenced renewable durable route leases, queue-only control orchestration, NOTIFY with five-second TimeProvider fallback, and retention worker.
- Added fenced renewable work ownership across vendor planning I/O. Latest exact policy validation and changed-only safe-subset checks hold disabled/stale/unsafe work visibly; unknown post-dispatch operation outcomes remain non-retryable for reconciliation.
- Removed the fixed NetSuite-to-Halo scheduler run/options/status/worker/lock classes and migrated Program, hosting registration, and host tests to `EntitySyncControlWorker`.

## Changed files

- `src/Application/LISSTech.EntitySync.Application.csproj`
- `src/Application/SyncScheduleService.cs`
- `src/Application/CanonicalChangeService.cs`
- `src/Hosting/EntitySyncHostingServiceCollectionExtensions.cs`
- `src/Runtime/PostgresSyncAuditRepository.cs`
- `src/Runtime/PostgresSyncOperationRepository.cs`
- `db/migrations/012_durable_scheduler_control.sql`
- `scheduler/LISSTech.EntitySync.Scheduler.csproj`
- `scheduler/Program.cs`
- `scheduler/PostgresSyncWorkQueue.cs`
- `scheduler/PostgresRouteLock.cs`
- `scheduler/EntitySyncControlWorker.cs`
- `scheduler/AuditRetentionWorker.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlSchedulerTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/CanonicalChangeServiceTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncSchedulerHostTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ConnectionDefinitionServiceTests.cs`
- Removed `scheduler/EntitySyncScheduledRun.cs`, `EntitySyncSchedulerOptions.cs`, `EntitySyncSchedulerStatus.cs`, `EntitySyncSchedulerWorker.cs`, `IEntitySyncSchedulerRunLock.cs`, `PostgresEntitySyncSchedulerRunLock.cs`, and `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncSchedulerTests.cs`.

## Self-review

- PostgreSQL is authoritative for schedules, receipts, work, route ownership, plan/approval/operation links, and redaction state; there is no process-local durable state.
- Due scheduling locks only DB rows inside one short transaction and atomically inserts idempotent work before fenced next-run advancement. Unsafe exact policy versions still produce visible work that the worker holds rather than silently advancing without a record.
- Canonical intake inserts receipt and deterministic work in one transaction; identical replay returns the existing receipt and conflicting identity reuse is rejected.
- Work and route ownership use DB clock, owner/token/attempt/expiry fences and renew through short database borrows; no DB transaction, connection, or advisory lock survives vendor I/O.
- Planning uses the work's exact policy and asserted canonical version. The version-pinned source adapter read never substitutes a newer entity.
- Retention updates only expired ciphertext, records redaction time, and preserves identity, hashes, expiry, and event/item metadata.
- Production DI resolves the new workers, schedule service, queue, route lock, and retention components; removed fixed scheduler symbols have no compatibility alias.

## Concerns

- Focused coverage uses deterministic in-memory fakes plus source/migration contract assertions; no live PostgreSQL integration environment was available in the exact Task 7 filter.

## Fix Round 1

### RED

- Started an isolated PostgreSQL 18 cluster on `127.0.0.1:5433` and ran the exact Task 7 filter with `DATABASE_URL` pointing at the live server.
- The live-PostgreSQL RED failed on the wished-for durable plan/approval/operation checkpoint and DB-clock deferral APIs (`artifact://1474`). This proved crash recovery and route-contention behavior were not implemented.
- After the checkpoint implementation, the live suite reached 18/19 and exposed the audit read-after-scrub fixture/contract path (`artifact://1484`).

### Fixes

- The canonical version-pinned OrchestraMSP read is now carried as a trusted internal planning input, included in the durable request digest, and used directly by `EntitySyncPlanner`; the planner does not issue a latest source query. Non-Orchestra policies and non-control public request paths cannot set this override.
- Durable control work now records a fenced plan ID and digest, approval ID, and operation ID as ordered checkpoints. Reclaims preserve checkpoint columns, recover deterministic plan/approval/operation identities, resume from the last durable result, and complete only when every stored identity matches.
- The approval recovery path uses a deterministic control approval ID and returns the existing exact digest-bound approval after a lost response instead of approving again. Operation queue recovery continues through its deterministic idempotency key.
- Canonical work is created only by the transaction that wins the canonical event insert. An identical replay reads the immutable original receipt/work links even after a later policy version is created.
- Route contention writes a DB-clock `not_before` deferral before releasing the work lease, allowing the next route to progress without hot spinning. Work selection ignores deferred rows until eligible.
- `EntitySyncOperationWorker` now acquires, renews, and releases the same durable tenant/route lease around vendor apply/reconciliation. The PostgreSQL lease uses short DB borrows and owner/token/attempt/DB-expiry fencing; no connection or transaction crosses vendor I/O.
- Audit and operation snapshot reads return unavailable (`null`) after scrub. Retention triggers reject identity, expiry, or metadata mutation, require complete ciphertext clearing after DB expiry, and stamp redaction with the DB clock while preserving hashes and parent metadata.
- The Task 9 production OrchestraMSP adapter remains intentionally out of scope. Until Task 9 registers a generation-pinned adapter implementing the existing exact-version contract, canonical work is held with `CANONICAL_VERSION_READER_UNAVAILABLE`.

### Live PostgreSQL GREEN

- Exact focused filter with the isolated live PostgreSQL server: 22 passed, 0 failed, 0 skipped, 1 second (`artifact://1493`).
- The filter now includes live PostgreSQL receipt replay across policy change, checkpoint/reclaim phases, route deferral and operation route fencing/renewal, audit and operation trigger mutation rejection, metadata-preserving scrub, and read-after-scrub behavior, plus a planner test proving the pinned source snapshot is not reread.
- Final scheduler Release build passed (`artifact://1495`).
- No formatter, linter, broad build, or project-wide test suite was run.

### Remaining concern

- The real OrchestraMSP generation-pinned adapter and its production registration remain assigned to Task 9 by the written durable-control-plane plan.
