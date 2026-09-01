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
