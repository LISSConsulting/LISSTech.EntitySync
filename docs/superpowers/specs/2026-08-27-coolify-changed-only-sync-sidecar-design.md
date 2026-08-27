# Coolify Changed-Only Sync Sidecar Design

## Problem

EntitySync can safely plan and apply NetSuite-to-HaloPSA synchronization, but it has no recurring scheduler. Reusing a previously approved plan is not valid because plans are immutable, expire, and are consumed exactly once. Running a full update plan every 12 hours would also rewrite every linked HaloPSA client even when NetSuite's mapped data has not changed.

The Coolify deployment needs a dedicated sidecar that creates a fresh update-only plan immediately after startup and every 12 hours thereafter. It must write only persistently linked customers whose mapped NetSuite payload changed after the last successful synchronization. It must not create clients, establish new links from fuzzy matches, retry writes immediately, or depend on MCP OAuth.

## Goals

- Run one fixed NetSuite `Customer` to HaloPSA `Client` route in the existing Coolify Compose deployment.
- Run immediately after startup, then 12 hours after each run completes.
- Include active and inactive NetSuite customers.
- Permit writes only for `Update` items with `MatchType=Linked`.
- Reconcile every linked customer on the first run, then persist a successful mapped-payload hash.
- Skip later writes whose mapped NetSuite payload, target identity, and hash schema version are unchanged.
- Persist change state in the existing PostgreSQL database.
- Prevent overlap during rolling deployments or accidental multiple replicas.
- Preserve the existing inspect, digest approval, connection-generation, and single-consumption safeguards.
- Expose safe aggregate status and emit redacted OpenTelemetry logs.

## Non-Goals

- Creating missing HaloPSA clients.
- Automatically accepting name-only, fuzzy, ambiguous, or reviewer-selected links.
- Detecting or repairing manual HaloPSA drift when NetSuite has not changed.
- Supporting multiple NetSuite/HaloPSA account pairs in one deployment.
- Persisting plans or in-flight apply operations across process restarts.
- Immediate retry of failed or uncertain writes.
- Adding an internal cron daemon to the MCP container.

## Architecture

Add a .NET 8 executable under `scheduler/` and a dedicated `entitysync-scheduler` service to `docker-compose.yaml`.

The scheduler references the existing Core, Ports, Application, Mapping, Matching, Adapters, Runtime, and a new shared Hosting composition project. It calls `EntitySyncService` directly; it does not call `/mcp` and receives no MCP OAuth configuration.

The MCP and scheduler containers independently own their in-memory connection and plan repositories. They share:

- PostgreSQL migrations and change state through `DATABASE_URL`.
- The same server-managed NetSuite and HaloPSA environment variables.
- Common adapter construction and application dependency registration from `src/Hosting`.
- The same mapping, planning, digest, approval, and apply implementations.

The scheduler is a persistent worker, not a one-shot cron container. It exposes an internal HTTP health/status endpoint and performs one run at a time. Its delay begins after a run finishes, so a slow run cannot overlap or compress the next interval.

## Shared Hosting Composition

Move server-managed adapter construction out of MCP tool code into `src/Hosting`.

`IServerManagedEntityAdapterFactory` creates adapters from environment-backed configuration and performs vendor-specific credential acquisition. The implementation supports every vendor currently exposed by MCP so the repository retains one server configuration convention. `ConnectionTools.ConnectVendor` delegates to this factory; the scheduler uses it for NetSuite and HaloPSA.

Common service registration also moves into Hosting so MCP and scheduler receive the same:

- `IEntityConnectionRepository`
- `IEntitySyncPlanRepository`
- `IEntityExclusionRepository`
- `IEntityChangeStateRepository`
- `IEntityMatcher`
- `IEntityMapper`
- `EntitySyncPlanner`
- `EntitySyncService`

MCP-specific OAuth, tools, and request context remain in `mcp/`. Scheduler lifecycle and status remain in `scheduler/`.

## Fixed Route

The sidecar has one compile-time synchronization policy:

- Scheduler tenant: `coolify-scheduler`
- Source connection ID: `netsuite`
- Source vendor/type: NetSuite `Customer`
- Target connection ID: `halopsa`
- Target vendor/type: HaloPSA `Client`
- Include inactive source entities: true
- Create missing targets: false
- Update policy: `ChangedLinkedUpdatesOnly`
- Interval: 12 hours after completion
- First run: immediate

Vendor account identity is still environment-driven. The scheduler refuses startup when required NetSuite, HaloPSA, PostgreSQL, or telemetry configuration is invalid.

## Persistent Change State

Add migration `002_entity_change_state.sql` and `IEntityChangeStateRepository`.

Each state row contains:

- Tenant ID
- Route fingerprint
- Source and target vendor, connection ID, and entity type
- Immutable NetSuite source entity ID
- Current linked HaloPSA target entity ID
- Hash schema version
- Last successfully applied mapped-payload SHA-256
- Source name for database diagnostics
- Last successful apply timestamp

The primary key is the tenant, route fingerprint, and source entity ID. The route fingerprint is SHA-256 over normalized, non-secret connection identity and route data: NetSuite account ID, HaloPSA base URL, stable connection IDs, vendors, and entity types. Credential rotation for the same accounts retains state; moving the deployment to another account pair creates a new state namespace automatically.

The database does not store mapped payloads, raw responses, credentials, access tokens, or vendor timestamps.

## Canonical Payload Hash

Add an application-level `EntityWriteRequestDigest` with an explicit hash schema version.

For update items, the digest covers the exact desired write request:

- Target vendor and entity type
- Target entity ID
- Primary site ID
- Desired name
- Mapped fields
- Mapped custom fields

Canonical serialization recursively sorts dictionary keys using ordinal comparison. Primitive values use invariant representation. Strings retain the normalized adapter/mapper value. The digest excludes timestamps, raw responses, secrets, object identity, and dictionary insertion order.

Changing mapper behavior or canonicalization increments the hash schema version. A version mismatch is treated as changed and causes one controlled reconciliation of every linked entity.

## Planning Policy

Add a strongly typed update policy to `CreateEntitySyncPlanRequest` and `EntitySyncPlanExecution`. Existing callers default to current planning behavior. The scheduler selects `ChangedLinkedUpdatesOnly`.

Under this policy:

| Match/result | Planned action |
|---|---|
| Linked and no state | `Update`; first-run reconciliation |
| Linked and target/hash/version changed | `Update` |
| Linked and target/hash/version unchanged | `None`, `MatchType=Unchanged` |
| Name-only high-confidence match | `None`; unattended linking prohibited |
| Ambiguous or review match | `None`; operator review required |
| Missing target or no match | `None`; create prohibited |

Every writable item carries its desired hash and hash schema version in the plan item. These fields are covered by `EntitySyncPlanDigest` but omitted from ordinary MCP plan views and scheduler status output.

The planner batch-loads state for the route and source IDs before finalizing actions. It does not perform one PostgreSQL query per entity.

## Successful-Write Checkpointing

`EntitySyncService.ApplyAsync` checkpoints change state only when all of the following are true:

- The plan execution policy is `ChangedLinkedUpdatesOnly`.
- The item action is `Update`.
- The target write returned success.
- The item contains the digest-covered desired hash and schema version.

Checkpointing upserts the source ID, linked target ID, desired hash, version, source name, and successful timestamp.

Failed or cancelled writes do not advance state. If the HaloPSA write succeeds but checkpointing fails, the item and plan report failure. No immediate retry occurs. The next 12-hour run may repeat the idempotent target update because suppression cannot be proven.

If checkpointing succeeds and the process terminates before the plan reaches terminal `Applied`, the next run sees the successful hash and skips that item. This avoids a duplicate write after a post-checkpoint crash.

## Scheduler Run Lifecycle

Each run performs these steps:

1. Open a dedicated PostgreSQL connection and acquire a non-blocking advisory lock derived from the scheduler tenant and route fingerprint.
2. If the lock is held, record `SkippedOverlap`, release resources, and wait 12 hours.
3. Construct and register fresh NetSuite and HaloPSA adapters under `netsuite` and `halopsa`. Fresh construction obtains a current HaloPSA access token.
4. Test both vendor connections.
5. Create a plan with `IncludeInactive=true`, `CreateMissing=false`, and `ChangedLinkedUpdatesOnly`.
6. Inspect every page through `EntitySyncService.GetPlan` using page size 100.
7. Fail closed if a writable row is not `Update + Linked`, or if any writable row lacks a desired hash/version.
8. Approve the exact fully inspected digest.
9. Apply sequentially with the application-stopping cancellation token.
10. Publish aggregate terminal status, dispose run resources, release the advisory lock, and schedule the next run for 12 hours after completion.

The scheduler creates a new plan every run. It never replays a previous plan ID.

## Concurrency and Restart Semantics

The in-process loop is strictly sequential. A PostgreSQL session advisory lock prevents a second scheduler replica or overlapping rolling deployment from running the same route concurrently. The lock is held on a dedicated connection and is automatically released if that connection or process dies.

A server shutdown cancels the current apply. The plan records terminal `Failed` with its completed prefix. Only successfully checkpointed items are suppressed on the next startup. The restarted sidecar runs immediately and safely reevaluates the route.

There are no automatic same-run or short-delay retries. Failed and uncertain items become eligible at the next normal 12-hour run.

## Health, Status, and Logging

The scheduler exposes internal HTTP endpoints:

- `/health`: liveness only; returns healthy when the process and scheduling loop are responsive.
- `/status`: bounded aggregate scheduler state.

Status fields:

- State: `Waiting`, `Running`, `Applied`, `Failed`, or `SkippedOverlap`
- Last start/completion and next-run timestamps
- Current/last plan ID
- Total, changed, unchanged, policy-skipped, succeeded, failed, and apply-skipped counts
- One bounded safe error summary

Vendor sync failure does not make `/health` unhealthy because Coolify restart would create an immediate retry loop. Invalid startup configuration, migration failure, or inability to initialize PostgreSQL terminates the process so Coolify can restart the container.

Logs use the existing OTLP exporter variables with default service name `lisstech-entitysync-scheduler`. Logs and status never include credentials, access tokens, raw responses, mapped payloads, or entity names.

## Compose and Container Security

Add a scheduler Dockerfile using the same digest-pinned .NET 8 SDK and ASP.NET runtime images as MCP. Publish framework-dependent multi-file output.

The Compose service:

- Depends on healthy `entitysync-db`.
- Shares only required PostgreSQL, telemetry, NetSuite, and HaloPSA environment variables.
- Exposes its health port only to the Compose network.
- Runs as the image non-root user.
- Uses a read-only root filesystem.
- Mounts only bounded `/tmp` tmpfs.
- Drops all Linux capabilities.
- Enables `no-new-privileges` and `init`.
- Uses `restart: unless-stopped`.

No Docker volume is required for scheduler state; PostgreSQL is authoritative.

## Failure Policy

| Failure | Behavior |
|---|---|
| Invalid required configuration | Fail startup |
| Database migration/init failure | Fail startup |
| Advisory lock unavailable | `SkippedOverlap`; no vendor calls |
| Vendor connection test failure | Run `Failed`; wait 12 hours |
| Plan contains prohibited writable action | Run `Failed`; no writes |
| Target write failure | Continue remaining items; do not checkpoint failed item |
| Checkpoint failure after target success | Item/run `Failed`; no immediate retry |
| Application shutdown | Cancel, preserve completed checkpoints, run again after restart |

All externally surfaced errors are safe summaries. Detailed exceptions go through existing structured logging without secrets or raw vendor bodies.

## Verification

Permanent behavioral tests cover:

- Canonical hash stability across dictionary ordering.
- Hash changes for each mapped request field, target identity, and schema version.
- A successful first run updates every linked entity once and stores each successful hash.
- After a successful baseline, an identical second run performs zero vendor writes.
- One mapped NetSuite field change updates only that source entity.
- Active and inactive sources are both evaluated.
- Name-only, ambiguous, missing-target, and unmatched entities never write.
- Failed target writes and failed checkpoints do not advance state.
- Successful checkpoint followed by interruption suppresses the completed item next run.
- Full-page inspection and exact digest approval occur before apply.
- A second scheduler cannot acquire the same route lock.
- Shutdown records safe partial progress.
- Health remains live after a run failure while status reports failure.

Deployment verification builds both images, validates Compose, starts PostgreSQL plus both services, and exercises a controlled route twice: the first run reconciles and checkpoints, and the second run reports unchanged with no target writes. Status must show the next run approximately 12 hours after completion.

## Acceptance Criteria

1. Coolify starts `entitysync-mcp`, `entitysync-scheduler`, and PostgreSQL from one Compose resource.
2. The scheduler runs immediately and then 12 hours after each completion.
3. A run attempts each persistently linked active or inactive NetSuite customer at most once; successful writes establish hashes and failed writes remain uncheckpointed.
4. After a successful baseline, an unchanged second run performs zero HaloPSA writes.
5. A mapped change to one NetSuite customer causes exactly one HaloPSA update.
6. No create, fuzzy link, ambiguous match, or unlinked entity can write unattended.
7. Only successful target writes with successful PostgreSQL checkpoints advance state.
8. Concurrent scheduler replicas cannot overlap the fixed route.
9. Vendor failures do not trigger immediate retries or Coolify restart loops.
10. Health, status, and logs remain bounded and secret-safe.
