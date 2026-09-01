# Task 6 Report: Durable sync operation execution

## Delivered

- Added tenant-scoped dry-run and apply queueing with deterministic request hashes, stable operation IDs, replay-safe idempotency, current control-state validation, and exact approval consumption plus operation-graph creation in one PostgreSQL transaction.
- Added a one-item leased worker. The worker pins and disposes source/target adapter leases, revalidates policy/generations and create exclusions under short PostgreSQL route locks, and commits redacted metadata, encrypted before evidence, deterministic vendor request ID, and `dispatch_started_at` before its only vendor call.
- Added explicit post-dispatch `Unknown` handling. Cancellation, timeout, process recovery, and checkpoint/audit failure never redispatch an item whose dispatch marker exists.
- Added fenced outcome reconciliation using request-ID proof first, immutable target identity second, and exact canonical desired-state readback last. Inconclusive outcomes remain `Unknown`; reconciliation attempt/owner/expiry fences use PostgreSQL time.
- Added a fenced reconciliation-evidence boundary so authoritative readback and the encrypted after snapshot are committed while the item remains `Unknown`, before changed-only checkpoint and success audit. A later checkpoint/audit failure therefore retains evidence without permitting redispatch.
- Added durable terminal counts/status derivation, safe idempotent audit writes, and 365-day encrypted before/after/full-value retention.
- Added migration `011_durable_operation_dispatch.sql`, repository operations, request/result request-ID metadata, and durable queue callsites in `EntitySyncService` and `EntitySyncApplyCoordinator`.

## RED evidence

The first focused run failed to compile because `SyncOperationService` and `EntitySyncOperationWorker` did not exist. After the initial implementation, focused failures exposed the immutable snapshot row blocking the one-time after-value fill, a concurrent replay validating consumed plan state before checking the existing operation, and missing durable after evidence when checkpointing failed. The checkpoint crash-window assertion specifically failed with `EncryptedAfterCiphertext` null before the fenced evidence method was added.

## Verification

Using a dedicated local PostgreSQL 16 instance and `DATABASE_URL`:

- `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~DurableOperationTests`
  - Passed: 9, Failed: 0, Skipped: 0.
- Individually named changed migration/repository checks:
  - `Migrations_create_control_plane_and_are_idempotent`
  - `Operation_graph_and_transitions_enforce_queue_identity_and_terminal_consistency`
  - `Operation_failed_partial_and_cancelled_terminal_paths_match_item_outcomes`
  - `Inspection_requires_exact_coverage_and_approval_is_single_use_and_expiring`
  - Passed: 4, Failed: 0, Skipped: 0.

The focused scenarios cover duplicate and concurrent apply, dry-run non-mutation, exact approval consumption, before/after encryption, changed exclusion and generation rotation, lost response, restart reconciliation, cancellation on both sides of dispatch, checkpoint failure evidence, stale operation/reconciliation leases, permanent unknown, terminal counts, deterministic redacted request IDs, and zero duplicate vendor writes.

## Self-review

- No database connection or route lock is held across vendor I/O.
- Every acquired adapter lease is asynchronously disposed.
- All item/reconciliation mutations are fenced by attempt, owner, and PostgreSQL lease time.
- `dispatch_started_at` is the no-redispatch boundary; reclaimed marked items enter reconciliation only.
- SQL and safe codes contain no raw vendor bodies, secrets, or unredacted entity values.
- Apply approval consumption and graph insertion are atomic; dry-run does not inspect or consume an approval.

## Concerns

None within Task 6 scope. Scheduler cron hosting, HTTP controllers, and the OrchestraMSP adapter remain intentionally out of scope.

## Fix Round 1

- Preserved the first randomized encrypted after snapshot and made repeated reconciliation validate the persisted after hash without re-protecting immutable evidence.
- Cut the MCP-attributed apply and status tools over to durable operation queueing/polling by operation ID, exact approval ID, and stable idempotency key.
- Registered the durable queue, worker, reconciler, and audit services in production hosting.
- Prioritized pending items ahead of unknown reconciliation work so one permanent unknown cannot starve later writes.
- Added PostgreSQL-time reconciliation lease renewals and ownership rechecks around checkpoint and audit side effects.
- Persisted returned create target IDs before readback/reconciliation.
- Removed before-state equality as proof of non-application and kept request-ID-applied outcomes unknown until authoritative readback is available.
- Corrected terminal derivation so skipped items do not turn failed/unknown-only runs into partial success.
- Moved lease, approval, and operation graph expiry eligibility to PostgreSQL `clock_timestamp()` with DB-relative lease durations.
- Changed the focused protector to randomized ciphertext and added skipped-plus-unknown terminal coverage.

Focused verification after these changes: `DurableOperationTests` passed 9/9 against PostgreSQL.

## Fix Round 2

- Cut every attributed plan lifecycle tool to durable state: explicit persisted policy ID and planning idempotency, durable page inspection, durable approval with returned approval ID, and operation-ID queue/status.
- Added one PostgreSQL reconciliation-success transaction fenced by current Unknown outcome, reconciliation attempt/owner, and unexpired database-time lease. Changed-only checkpoint, idempotent encrypted audit, item success, and terminal run refresh now commit or roll back together.
- Corrected the normal attempt classifier so skipped items do not count as successful writes when failed or unknown outcomes remain.
- Stopped desired-state global fallback whenever an immutable returned or planned target ID exists.
- Added focused coverage for the attributed durable lifecycle, immutable-ID mismatch, skipped-plus-failed classification, and a checkpoint delay that expires the reconciliation lease and proves checkpoint/audit/item rollback.

Focused verification: `DurableOperationTests` passed 11/11 against PostgreSQL; `PlatformTests.McpFocusedPlanUsesBoundedQueryAndExactSourceId` passed 1/1 for the unannotated compatibility helper.

## Fix Round 3

- Registered `PlanManifestBuilder` and `DurablePlanService` as production singletons so MCP-attributed durable plan tools can be activated from the platform service provider.
- Extended the production host-mode resolution test to resolve the complete durable planning dependency chain.

Focused RED/GREEN verification: the named hosting test first failed because `PlanManifestBuilder` was unregistered, then passed 1/1 after both registrations were added.
