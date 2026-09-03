# Task 8 report — OAuth-protected typed control API

## RED / GREEN evidence

- RED: `dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~ControlApiTests --no-restore` failed because the ControlApi contracts/routes did not exist (`artifact://1595`).
- Initial GREEN: the focused filter completed with **28 passed, 0 failed, 0 skipped** (`artifact://1628`).
- Review GREEN: the focused filter completed with **29 passed, 0 failed, 0 skipped** after explicit DB-only mode, policy-recovery boundary, and Bearer challenge coverage (`artifact://1670`).
- Live PostgreSQL GREEN against the isolated Task 7 cluster: the atomic crash rollback/recovery test and blocked-external-call connection-release test completed with **2 passed, 0 failed, 0 skipped** (`artifact://1658`).
- Minimal production compile: `dotnet build mcp/LISSTech.EntitySync.Mcp.csproj --no-restore --verbosity:minimal` succeeded (`artifact://1668`).

## HTTP inventory and permission table

All routes are below `/api/v1/control`, require an authenticated validated request context, and have stable unique operation IDs.

| Permission | Routes |
|---|---|
| `EntitySync.Read` | `GET /connections`, `GET /connections/{connectionId}`, `GET /policies`, `GET /policies/{policyId}/versions`, `GET /plans`, `GET /plans/{planId}/items`, `GET /runs`, `GET /runs/{runId}`, `GET /runs/{runId}/items`, `GET /schedules`, `GET /audit`, `GET /exclusions`, `GET /capabilities`, `GET /entities`, and protected `GET /openapi/v1.json` |
| `EntitySync.Manage` | `POST /connections`, `PATCH /connections/{connectionId}`, `DELETE /connections/{connectionId}`, `POST /connections/{connectionId}/test`, `POST /policies`, `POST /policies/{policyId}/versions`, `POST /schedules`, `POST /schedules/{scheduleId}/versions`, `POST /exclusions`, `DELETE /exclusions` |
| `EntitySync.Operate` | `POST /plans`, `POST /plans/{planId}/inspections`, `POST /plans/{planId}/dry-run` |
| `EntitySync.Approve` | `POST /plans/{planId}/approvals`, `POST /plans/{planId}/apply` |
| `EntitySync.Audit` | `GET /audit/{eventId}/values` |
| `EntitySync.Expert` | `POST /expert/suiteql`, `POST /expert/custom-properties` |
| workload-only canonical intake | `POST /canonical-changes`; requires an app token with `roles=EntitySync.Operate`, exactly one `tid`, exactly one `azp`, no delegated `scp`, and an allowlisted `azp` |

`GET /health` is unauthenticated liveness. `GET /health/ready` checks database migration 013, data-protection key-ring access, and the durable control worker heartbeat only; it does not probe vendors.

## Implementation summary

- Central request context enforces exactly one tenant and exactly one actor: delegated `oid` with `scp`, or workload `azp` with `roles`; mixed/conflicting/multiple claim identities fail closed.
- Every POST/PATCH/DELETE uses one endpoint filter backed by the durable idempotent command executor. Identity is tenant + key; the stable request hash covers normalized method, route, sorted query, and canonical typed request body. Replay returns the stored body/status exactly; changed input conflicts with 409. The response is emitted only after durable executor completion.
- DB-only connection, schedule, and exclusion mutations explicitly use `AtomicDatabase` mode: their PostgreSQL repository effect and receipt completion enlist in one transaction. A crash before completion rolls both back; a live PostgreSQL test proves recovery produces one effect.
- Adapter/planning/policy-validation/queue/canonical/expert calls remain `Recoverable` and never hold a database transaction or connection while awaiting external work. Policy creation uses a stable execution-token-derived ID and versions recover by their deterministic next version before redispatch. A live PostgreSQL test blocks an external callback and verifies `pg_stat_activity` has no other connection. Concurrent completion races return the already-stored safe response.
- Expert custom-property writes use authoritative N-central property readback for an incomplete-receipt recovery. Exact value match reconstructs success without redispatch; absent/different/unreadable outcomes fail permanently with `IDEMPOTENCY_RECOVERY_UNKNOWN` rather than blindly writing again.
- Typed request/response records cover connections, policies/exclusions, plans, runs, schedules, audit, capabilities/entities, and expert operations. Expert raw rows are bounded by `MaximumRows` and only use the existing expert adapter capability.
- Signed, versioned data-protection cursors bind resource and tenant. Page size is restricted to 1–100.
- Exception mapping returns RFC 9457 problem details with stable `code` and correlation ID, plus operation/run IDs when available; internal exception/vendor/secret bodies are neither returned nor logged by the HTTP exception boundary. Unauthorized API/OpenAPI responses emit a valid `WWW-Authenticate: Bearer` challenge.
- OpenAPI is registered and mapped in HTTP mode only, protected by `EntitySync.Read`, with endpoint-name-based stable operation IDs and typed schemas. Stdio composition is unchanged.

## Task-scoped extra files

The following files beyond the brief's named ControlApi/Program/project/test files were genuinely required to connect the complete surface to Tasks 3–7 rather than add placeholder handlers:

- `db/migrations/013_control_api_readiness.sql`: durable worker heartbeat readiness state.
- `scheduler/PostgresSyncWorkQueue.cs`: writes the heartbeat while the durable worker accesses its queue.
- `src/Application/ExpertOperationService.cs`, `EntitySyncDependencyUnavailableException.cs`, and `EntitySyncIdempotencyRecoveryUnknownException.cs`: bounded expert facade, safe dependency translation, and fail-closed external recovery.
- `src/Application/DurablePlanService.cs`: exposes existing tenant-qualified plan and approval reads to the HTTP facade.
- `src/Hosting/EntitySyncHostingServiceCollectionExtensions.cs`: registers the expert application service and control dependencies.
- `src/Ports/IEntityAdapter.cs`, `src/Adapters/NetSuite/NetSuiteEntityAdapter.cs`, `src/Adapters/NCentral/NCentralEntityAdapter.cs`: narrow capability interfaces, bounded expert operations, and authoritative custom-property readback; no generic vendor-body dumping.
- `src/Ports/IEntitySyncDataProtector.cs`: stable execution token plus explicit recoverable/atomic-database execution mode.
- `src/Ports/IDurableSyncPlanRepository.cs`, `ISyncOperationRepository.cs`, `ISyncPolicyRepository.cs`, `ISyncScheduleRepository.cs`, `ISyncAuditRepository.cs`: tenant-qualified list/page/audit contracts.
- `src/Runtime/PostgresControlTransaction.cs`, `PostgresIdempotencyRepository.cs`, `PostgresConnectionDefinitionRepository.cs`, `PostgresEntityExclusionRepository.cs`, and `PostgresSyncScheduleRepository.cs`: atomic DB-only effect/receipt enlistment and connection-free external callback execution.
- `src/Runtime/PostgresDurableSyncPlanRepository.cs`, `PostgresSyncOperationRepository.cs`, and `PostgresSyncAuditRepository.cs`: tenant-qualified reads and expiry-safe audit full-value reads.
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlRepositoryTests.cs`: live PostgreSQL crash rollback and external callback connection-release proofs required by recovery review.
- `db/migrations/014_idempotency_execution_leases.sql`: durable execution ownership, fencing attempts, database-clock leases, byte-exact response storage, and policy execution-token binding.
- `src/Application/SyncPolicyService.cs`, `src/Runtime/PostgresSyncPolicyRepository.cs`, and `src/Ports/ISyncPolicyRepository.cs`: exact policy/version recovery by the stable idempotency execution token rather than by an adjacent version guess.
- `src/Application/ConnectionDefinitionService.cs` and `EntitySyncPlanner.cs`: translate adapter acquisition, connection-test I/O, and source/target planning reads through the safe dependency boundary while preserving cancellation and application validation.
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlSchedulerTests.cs`, `DurablePlanServiceTests.cs`, and `SyncPolicyServiceTests.cs`: existing policy repository fakes migrated to the required token-bound repository contract.

## Self-review

- Verified all 33 business routes are present, OAuth-protected, and carry exactly the expected policy metadata.
- Verified both delegated scopes and application roles, ambiguous identities, canonical workload restrictions, required idempotency, exact replay, hash conflict, and tenant-separated keys.
- Verified only the seven DB-only route/method pairs use the ambient atomic transaction mode; vendor/adapter/policy-validation/planning/queue paths remain recoverable without an open DB connection.
- Verified cursor failure/page bound behavior, safe problem bodies, protected OpenAPI operation/schema inventory, readiness response semantics, Bearer challenges, 204 exclusion deletion, expired audit-value denial, safe dependency mapping, and expert readback recovery behavior.
- Fixed a GREEN-cycle defect where reading `HttpRequest.Body` inside an endpoint filter occurred after minimal-API model binding had consumed the stream; hashing now uses the already-bound typed request contract, preserving canonical semantic hashing without buffering or rereading the request.

## Concerns

- The focused WebApplicationFactory suite substitutes query/readiness/idempotency ports for deterministic HTTP boundary coverage. The critical idempotency transaction behavior is additionally proven against the isolated live PostgreSQL cluster.
- N-central is the only custom-property expert adapter. Its authoritative SOAP readback is exact and fail-closed; if the vendor cannot return the property or the value differs, the API reports permanent unknown recovery and requires operator reconciliation instead of redispatch.
- Existing unrelated test-project warnings remain; the 30-test focused WebApplicationFactory suite, eight live PostgreSQL repository/lease cases, two planner dependency-boundary cases, production provider resolution, and MCP build succeed.

## Review fix round

The post-implementation review identified that an incomplete durable receipt was only an audit marker: a concurrent request or retry could enter the callback again. Migration 014 and `PostgresIdempotencyRepository` now implement an execution state machine keyed by tenant and idempotency key:

- A row-locked claim records an opaque UUID owner, monotonically increasing attempt, and database-clock lease expiry before callback execution.
- A live same-hash claim waits with cancellation-aware bounded polling and never invokes the callback. A completed claim replays the persisted status and exact response bytes.
- The owner renews through short-lived heartbeat connections. Recoverable callbacks hold no database connection. Atomic-database callbacks retain only the intentional effect/receipt transaction.
- Lost ownership cancels and awaits the callback; completion is fenced by tenant, key, request hash, owner, attempt, and an unexpired database-clock lease. A stale owner cannot publish a receipt.
- An expired or released claim is taken over once with `IsRecovery=true`; the stable downstream token remains tenant/key/request-hash derived across attempts.
- Policy create/version rows persist that execution token. Recovery reads the exact token-bound row, so an intervening version created by another key can never be mistaken for the original request.

### Additional RED / GREEN evidence

- RED: the first 25-contender regression run invoked the callback once but exposed that PostgreSQL `jsonb` reordered replay response keys, making 24 replay bodies differ byte-for-byte from the first response (`artifact://1694`). Migration 014 changes the receipt body to JSON-validated text, preserving exact bytes.
- GREEN: 25 simultaneous same-key contenders, forced heartbeat ownership loss/cancel-and-await/fencing, one post-expiry recovery, atomic rollback, connection-free recoverable execution, direct receipt compatibility, all 29 ControlApi tests, and policy token binding completed with **35 passed, 0 failed, 0 skipped** (`artifact://1713`).
- GREEN: production HTTP service-provider resolution completed with **1 passed, 0 failed, 0 skipped** (`artifact://1706`).
- GREEN: minimal MCP production compile succeeded with no errors (`artifact://1715`); the post-cutover focused test project also compiled with no errors (`artifact://1711`).

### Final review corrections

- RED: an atomic owner whose lease was replaced could return after the takeover receipt committed and adopt that receipt, committing its own different database effect (`artifact://1729`). Atomic completion now treats every fenced completion miss as ownership loss and rolls back; only recoverable execution may adopt a completed receipt. Both stale-owner return and callback-exception interleavings pass and prove only the takeover effect persists (`artifact://1731`).
- RED: source and target planner adapter failures escaped as raw `InvalidOperationException`, which the HTTP boundary classified as `409` (`artifact://1733`). Both reads now preserve cancellation but translate dependency failures to the safe typed boundary; same-key retry proves the durable creation claim is released (**2 passed**, `artifact://1735`).
- RED: the safe `503` response test exposed that the framework exception middleware still logged the raw inner vendor text (`artifact://1741`). The control API now catches and maps before framework exception logging, aborting an already-started response rather than logging internals. The response/log regression passes with `DEPENDENCY_UNAVAILABLE`, correlation ID, and no vendor or exception detail (`artifact://1743`).
- GREEN: the final focused surface completed with **40 passed, 0 failed, 0 skipped**: all 30 `ControlApiTests`, eight live PostgreSQL idempotency/policy cases, and both planner source/target dependency cases (`artifact://1745`).
- GREEN: production HTTP service-provider resolution completed with **1 passed, 0 failed, 0 skipped** (`artifact://1749`), and the minimal MCP production compile succeeded with no errors (`artifact://1747`).
