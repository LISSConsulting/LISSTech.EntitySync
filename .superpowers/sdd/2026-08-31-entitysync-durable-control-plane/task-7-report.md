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

## Fix Round 2

### RED

- The focused live-PostgreSQL filter failed to compile after adding the wished
  operation renewal contract (`artifact://1503`), proving that operation ownership
  had no durable heartbeat API.
- A legacy canonical receipt regression fixture seeded a receipt whose persisted
  identity differs from the current deterministic candidate, so replay could prove
  that the stored identity is authoritative.

### Fixes

- Canonical replay now returns the persisted receipt ID and its persisted work links;
  the deterministic candidate is used only for the first insert.
- `ISyncOperationRepository.TryRenewLeaseAsync` and its PostgreSQL implementation
  renew only a live `Leased`/`Running` row fenced by tenant, operation, attempt,
  owner, and DB-clock expiry.
- `EntitySyncOperationWorker` now renews operation ownership and route ownership
  together. False results or exceptions from either renewal cancel the linked
  vendor-I/O token; the worker awaits that cancellation before disposing the route
  lease, observes renewal faults, and cannot mutate through the stale operation fence.
- Existing phase recovery was rechecked: recovered plan checkpoints use durable
  plan pages without calling the planner, recovered approval checkpoints skip
  approval creation, and recovered operation checkpoints complete the work link;
  pre-checkpoint crashes replay deterministic plan/approval/operation identities.
- Added live-PostgreSQL coverage for stored legacy receipt replay, repository lease
  renewal/takeover fencing, and delayed vendor I/O driven by TCS plus manual time,
  including forced ownership loss with exactly one dispatch.
- A focused renewal-exception regression first failed because vendor I/O remained
  live until the test timeout (`artifact://1518`), then passed after renewal faults
  were made fail-closed (`artifact://1520`).

### GREEN

- Exact focused filter against isolated PostgreSQL 18: 26 passed, 0 failed,
  0 skipped, 1 second (`artifact://1522`).
- Minimal scheduler Release build passed (`artifact://1524`).
- No formatter, linter, broad build, Pester, or project-wide test suite was run.

## Fix Round 3

### RED

- Worker-level live-PostgreSQL fixtures seed the exact lost-response states: an
  approval committed while work remains at `Planned`, and an operation committed
  while work remains at `Approved`. Reintroducing the prior unconditional
  `GetPageAsync` call left both at `Planning` (2 failed, `artifact://1535`).

### Fixes

- Control recovery now loads and validates the persisted plan identity, digest,
  policy version, route, and status before choosing a phase.
- Only `Draft` work without an approval is paged, safe-subset revalidated, and
  deterministically approved.
- `Approved` work with a lost approval response recovers the deterministic,
  digest-bound approval and checkpoints it without page inspection or another
  approval mutation.
- Work with an approval checkpoint validates that exact approval and queues or
  replays the deterministic operation without page inspection.
- `Consumed` work with a lost operation response replays the existing
  idempotency-bound operation and checkpoints the same ID. Work with an operation
  checkpoint validates the approval and operation identities before atomic linkage
  completion. Contradictory status/identity combinations are visibly held with
  `CONTROL_WORK_CHECKPOINT_CONFLICT`.
- Worker-level live-PostgreSQL recovery tests pass for all three reclaim phases
  (3 passed, `artifact://1539`) and assert one approval and one operation.

### GREEN

- Exact focused filter against isolated PostgreSQL 18: 29 passed, 0 failed,
  0 skipped, 3 seconds (`artifact://1541`).
- Minimal scheduler Release build passed (`artifact://1543`).
- No formatter, linter, broad build, Pester, or project-wide test suite was run.

## Fix Round 4

### RED

- Reintroducing the latest-policy gate before committed-operation recovery caused
  the stale-policy worker fixture to hold an already durable operation instead of
  linking it (`artifact://1556`).
- The wrong-plan approval fixture initially remained `Planning`, demonstrating
  that a thrown approval conflict bypassed the visible contradiction hold
  (`artifact://1552`).

### Fixes

- Persisted plan identity and committed `Consumed`/operation-checkpoint recovery
  now run before latest-policy usability checks. A disabled or newer policy cannot
  orphan an operation already created atomically with approval consumption.
- The recovery router always replays the deterministic apply request, even for an
  operation checkpoint, so the persisted mode, plan, approval, idempotency key, and
  request hash are validated before linkage.
- Missing consumed operations, wrong-plan/digest approvals, and operation
  idempotency conflicts are fenced into `Held` with
  `CONTROL_WORK_CHECKPOINT_CONFLICT` in one attempt instead of lease cycling.
- Live-PostgreSQL worker fixtures prove stale-policy recovery links the exact
  existing operation with one operation and one work row, while wrong approval and
  missing consumed-operation contradictions hold without creating an operation
  (`artifact://1554`).

### GREEN

- Exact focused filter against isolated PostgreSQL 18: 31 passed, 0 failed,
  0 skipped, 2 seconds (`artifact://1558`).
- Minimal scheduler Release build passed (`artifact://1560`).
- No formatter, linter, broad build, Pester, or project-wide test suite was run.
