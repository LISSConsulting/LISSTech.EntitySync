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
