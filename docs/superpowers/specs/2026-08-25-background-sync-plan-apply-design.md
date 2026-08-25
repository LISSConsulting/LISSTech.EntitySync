# Background Sync Plan Apply Design

## Problem

`apply_sync_plan` currently awaits the entire vendor write loop and passes the MCP request cancellation token into `EntitySyncService.ApplyAsync`. A client or proxy deadline therefore cancels active vendor writes. The service transitions the consumed plan from `Applying` to terminal `Failed`, may leave partial writes, and exposes no trustworthy progress or partial-result record. Large approved plans reliably exceed the observed 30-second MCP deadline.

## Goals

- Start an approved write exactly once and return within the MCP request deadline.
- Continue execution after the initiating client disconnects.
- Let clients poll trustworthy aggregate progress and terminal results.
- Preserve approval digest validation, permanent-exclusion validation, connection generation pinning, and single-consumption semantics.
- Report partial progress when execution fails or is cancelled by server shutdown.

## Non-goals

- Surviving an MCP server process restart.
- Persisting plans or apply operations in PostgreSQL.
- Retrying failed vendor writes automatically.
- Changing matching, mapping, approval, or exclusion behavior.
- Changing the synchronous read-only dry-run contract.

## Architecture

Add a singleton in-memory apply coordinator keyed by normalized tenant ID and plan ID. It owns each background task, its coordinator cancellation token, and an immutable progress snapshot. The coordinator is the only MCP-facing path that starts a write apply.

`apply_sync_plan(apply=true)` asks the coordinator to start the approved plan. The coordinator atomically registers the operation before executing it, preventing duplicate starts. The tool returns an `Applying` snapshot immediately instead of awaiting vendor writes. Calling the start operation again for the same plan returns the existing snapshot and never starts another task.

The coordinator invokes `EntitySyncService.ApplyAsync` with a coordinator-owned token. MCP request cancellation affects only the start response; it does not cancel committed background work. The application-stopping token cancels active operations because restart recovery is out of scope.

Add `get_sync_plan_apply` to read an operation snapshot without side effects. The snapshot contains:

- plan ID and plan status;
- total and processed item counts;
- succeeded, failed, and skipped counts;
- start and completion timestamps;
- concise failed-item details, without returning every successful item.

Terminal operation snapshots remain available while their in-memory plan remains available. Opportunistic cleanup removes snapshots whose plans have expired or disappeared.

## Service Changes

Extend `EntitySyncService.ApplyAsync` with optional progress reporting. After each skipped or attempted item, the service publishes an immutable progress value containing the aggregate counts and any new failure detail. Existing callers that omit progress retain current behavior.

The service continues to own domain state transitions:

```text
Draft -> Approved -> Applying -> Applied
                              -> Failed
```

The coordinator never changes plan status directly. It derives operation status from the service result or exception and verifies the repository's terminal plan status for polling.

Per-item vendor errors remain recorded and processing continues, as today. Request cancellation no longer reaches the service after start. Application shutdown cancellation reaches the service, which records the processed prefix and transitions the plan to `Failed`.

## MCP Contracts

### `apply_sync_plan`

- `apply=false`: execute the existing synchronous dry run.
- `apply=true`: synchronously reject malformed input and a plan that is already consumed without a registered operation; otherwise atomically register background execution and return the current operation snapshot.
- A repeated `apply=true` call for the same registered operation returns its existing snapshot.
- Approval digest, connection generation, and exclusion validation remain final service-side write gates. A failure discovered after registration becomes a terminal `Failed` snapshot with no automatic retry.

### `get_sync_plan_apply`

- Requires a plan ID.
- Returns `Applying`, `Applied`, or `Failed` with aggregate progress.
- Returns a safe not-started error when no apply operation exists.
- Does not start, retry, approve, or mutate a plan.

## Concurrency and Safety

- A concurrent dictionary and atomic registration establish one operation per tenant-plan key.
- The background task is always observed by the coordinator; exceptions become terminal snapshots.
- Progress snapshots are replaced atomically and never expose a mutable results collection.
- The plan repository's `Approved -> Applying` compare-and-transition remains the final write gate.
- No automatic retry occurs after failure because vendor writes may not be idempotent.

## Error Handling

- Client disconnect after a successful start response: operation continues.
- Client disconnect before receiving the response: a repeated start call returns the same operation rather than duplicating writes.
- Validation failure after operation registration: no vendor write starts, and polling reports terminal `Failed` with a safe reason.
- Per-item vendor failure: execution continues, failure count/details update, terminal plan status is `Failed`.
- Application shutdown: operation cancellation records the processed prefix and terminal `Failed` state.
- Unexpected coordinator failure: observe the exception, retain aggregate progress, and expose `Failed` without credentials or raw vendor responses.

## Verification

Regression tests will prove:

1. Cancelling the MCP request after starting does not cancel the vendor write.
2. The start call returns before a blocked vendor write completes.
3. Concurrent or repeated starts execute each write once.
4. Polling advances from `Applying` to `Applied` with correct counts.
5. Coordinator shutdown cancellation produces `Failed` with accurate partial progress.
6. Failed vendor writes are summarized without exposing successful-item payloads.
7. Existing approval, connection pinning, exclusion, dry-run, and apply-once tests remain green.

A smoke scenario will start a deliberately blocked multi-item apply through the MCP tool surface, cancel the initiating request, release the writes, and verify the polling tool reports terminal `Applied` with each write executed once.
