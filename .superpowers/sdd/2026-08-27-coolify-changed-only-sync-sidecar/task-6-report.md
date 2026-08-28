# Task 6 Report: Scheduler Run Orchestration, Locking, and Status

## Status

Complete. Task 6 scheduler library/core/project files and focused tests are implemented. No scheduler host, Program, Dockerfile, Compose, deployment configuration, or operations documentation was added.

## Interfaces, Ownership, and Lifetimes

- `EntitySyncScheduledRun.RunAsync(CancellationToken)` returns an immutable `EntitySyncSchedulerStatusSnapshot`. The run owns only per-run orchestration; it reuses the shared application service, connection repository, plan repository, server-managed adapter factory, and `TimeProvider`.
- `EntitySyncSchedulerStatus` is the singleton atomic status holder expected by the later HTTP host. `Snapshot` returns one immutable record containing exactly state, last-start/completion, next-run, plan ID, total, changed, unchanged, policy-skipped, succeeded, failed, apply-skipped, and one error. Publication uses `Interlocked`/`Volatile`; errors are capped at 512 characters.
- `IEntitySyncSchedulerRunLock.TryAcquireAsync` returns a nullable async lease. `PostgresEntitySyncSchedulerRunLock` opens a dedicated Npgsql connection and uses `pg_try_advisory_lock(hashtextextended(@route_key, 0))`. A successful lease exclusively owns that open connection. Disposal attempts `pg_advisory_unlock` with `CancellationToken.None` and always disposes the connection in `finally`.
- The run acquires the route lock before creating a vendor adapter. It creates fresh NetSuite and HaloPSA adapters on every run, reserves each fixed connection ID through repository admission, and transfers successful registrations to the repository. A factory-created adapter that cannot be registered is disposed by the run. Successfully registered adapters are repository-owned; replacement disposes the prior generation after outstanding leases end, and repository disposal owns the final generation.
- NetSuite and HaloPSA connection probes run independently after non-cancellation false/exception outcomes so both fresh connections are tested. Cancellation propagates immediately.
- `EntitySyncSchedulerWorker` is a persistent `BackgroundService`. It runs once before delaying, computes `nextRunAt` from completion, and uses `Task.Delay(TimeSpan.FromHours(12), timeProvider, stoppingToken)`. A failed run remains in-process and receives no early retry.

## One-Run Workflow

1. Derive the fixed route key from tenant `coolify-scheduler` and the Hosting-provided change-state scope, then try the advisory lock.
2. Return `SkippedOverlap` before any vendor construction when the lock is unavailable.
3. Create/register fresh `netsuite` and `halopsa` adapters and test both connections.
4. Create a NetSuite `Customer` to HaloPSA `Client` plan with fixed connection IDs, inactive sources included, create-missing disabled, and `ChangedLinkedUpdatesOnly`.
5. Inspect every page at 100 items, requiring stable digest and total across all pages.
6. Read a repository snapshot, require the exact fixed route/policy/no-create metadata, require its digest to equal the inspected digest, allow only `None` or `Update`, and require every update to be `Linked` with a target, lowercase SHA-256 desired hash, and the exact current hash schema version.
7. Approve the exact inspected digest and apply through the existing sequential application service.
8. Publish aggregate terminal counts without names or payloads. Non-cancellation exceptions become stage-specific fixed summaries; structured logs contain only exception type and a fixed safe message.
9. On cancellation, publish a safe failed snapshot and rethrow. Completed checkpoint progress remains visible. A cancellation-preserving lease wrapper prevents cleanup failure from replacing the in-flight cancellation while still invoking the lease exactly once.

## TDD Evidence

### Initial red

The scheduler tests and project reference were added before scheduler production files.

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncSchedulerTests --no-restore
```

Exit code: `1`

Expected failure excerpt:

```text
warning MSB9008: The referenced project ../../scheduler/LISSTech.EntitySync.Scheduler.csproj does not exist.
EntitySyncSchedulerTests.cs(8,27): error CS0234: The type or namespace name 'Scheduler' does not exist in the namespace 'LISSTech.EntitySync'
```

### Focused red/green corrections

A fixed-route metadata regression was verified red before adding the missing no-create snapshot validation:

```text
Failed EntitySyncSchedulerTests.CreateMissingPlanMetadataFailsClosedBeforeApply
Expected: "Failed"
Actual:   "Applied"
```

A connection-probe exception regression was verified red before independent probe capture was implemented:

```text
Failed EntitySyncSchedulerTests.TargetConnectionIsStillTestedWhenSourceProbeThrows
Expected: [1, 1]
Actual:   [1, 0]
```

A cancellation/lease-cleanup regression was verified red before cancellation-preserving cleanup was implemented:

```text
Failed EntitySyncSchedulerTests.CancellationPropagatesWhenAdvisoryLeaseCleanupFails
Assert.ThrowsAny() Failure: No exception was thrown
Expected: typeof(System.OperationCanceledException)
```

The final 23 scheduler cases cover baseline/unchanged suppression, one mapped change, inactive sources, overlap before factory calls, fresh-generation ownership/disposal, failed registration cleanup, false and throwing connection probes, name-only policy skips, strict writable hash/version/match validation, prohibited actions, fixed no-create metadata, all-page inspection, digest drift, partial-checkpoint cancellation, cleanup-failure cancellation, bounded safe status/logging, immediate execution, completion-based delay, and failure without early retry or worker exit.

## Verification Commands and Exact Outputs

Scheduler suite:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --no-restore --filter FullyQualifiedName~EntitySyncSchedulerTests
```

Exit code: `0`

```text
Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23, Duration: 280 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

Scheduler plus changed-only planning/apply regressions:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --no-restore --filter "FullyQualifiedName~EntitySyncSchedulerTests|FullyQualifiedName~ChangedOnlyPlanningTests|FullyQualifiedName~ChangedOnlyApplyTests"
```

Exit code: `0`

```text
Passed!  - Failed:     0, Passed:    49, Skipped:     0, Total:    49, Duration: 258 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

No formatter, linter, broad validation, project-wide test suite, deployment build, MCP call, or live-vendor operation was run.

## Self-Review

- Re-read the Task 6 brief and checked each workflow step and named scenario against production code and a focused behavioral test.
- Confirmed the Task 7 consumer contract is preserved: `EntitySyncSchedulerStatus` is the singleton holder and exposes immutable `.Snapshot`.
- Confirmed the advisory lock precedes adapter construction, holds a dedicated session connection for the full run, unlocks without caller cancellation, and disposes even after explicit unlock failure.
- Confirmed adapter ownership transfers only after successful repository registration; replacement and final disposal remain repository responsibilities.
- Confirmed both vendor probes run after non-cancellation failure/false outcomes, while cancellation is never converted into an ordinary failure.
- Confirmed every inspection page records the same digest before repository-snapshot validation and exact-digest approval.
- Confirmed only linked updates with exact checkpoint metadata can reach apply; `Create`, `Link`, `Review`, unknown actions, altered route/policy, and create-missing metadata fail closed.
- Confirmed status/log messages never contain entity names, raw exception messages, mapped payloads, credentials, tokens, or vendor response bodies.
- Confirmed worker timing is immediate-first and completion-based, and a failed run stays alive until the same 12-hour interval expires.
- Independent review initially found two Important edge cases: a throwing source probe skipped the target probe, and lease-cleanup failure could replace cancellation. Both received red tests and fixes. Independent re-review reported no findings and assessed the current implementation ready with confidence `0.98`.

## Commit

- `8107b2a45db94d7cacb019dc0f0a2594f10bd6d2` — `feat: orchestrate recurring changed-only sync`

## Concerns

No identified Task 6 correctness concern. The workstation has only the .NET 10 runtime while tests target `net8.0`, so focused execution used `DOTNET_ROLL_FORWARD=Major`. A live PostgreSQL two-session acquire/deny/release exercise is intentionally deferred to the later integration task; Task 6 used the required fake lock for deterministic orchestration tests and compiled the real Npgsql lease implementation.
