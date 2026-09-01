# Task 10 — Control Surface Convergence Report

## Route and service parity

All seven remote operations now translate into `IEntitySyncControlCommands`, implemented by Application-layer `EntitySyncControlCommands`.

| Operation | MCP translator | HTTP translator | Shared Application service |
|---|---|---|---|
| `connections.list` | `ConnectionTools.ListConnections` | `ControlHttpOperations.ListConnectionsAsync` | `ConnectionDefinitionService` |
| `plans.create` | `SyncTools.CreateSyncPlan` | `ControlHttpOperations.CreatePlanAsync` | `DurablePlanService` |
| `plans.inspect` | `SyncTools.GetSyncPlan` | `ControlHttpOperations.InspectPlanAsync` | `DurablePlanService` |
| `plans.approve` | `SyncTools.ApproveSyncPlan` | `ControlHttpOperations.ApprovePlanAsync` | `DurablePlanService` |
| `runs.dry-run` | `SyncTools.ApplySyncPlan(false)` | `ControlHttpOperations.QueueDryRunAsync` | `SyncOperationService` |
| `runs.apply` | `SyncTools.ApplySyncPlan(true)` | `ControlHttpOperations.QueueApplyAsync` | `SyncOperationService` |
| `exclusions.list` | `ExclusionTools.ListEntityExclusions` | `ControlHttpOperations.ListExclusionsAsync` | `EntityExclusionService` |

`McpRequestContext` and `ControlRequestContext` remain transport/auth translators. HTTP handlers no longer contain a second orchestration path. Remote apply persists and returns the queued durable operation ID/status immediately; it does not wait for vendor work.

## RED evidence

### C# parity RED

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~ControlSurfaceParityTests --no-restore
```

Initial exit 1, `artifact://1991` (reconfirmed as `artifact://1993`): the parity test could not compile because the desired shared Application contract/HTTP translator and result types did not exist.

### Named Pester RED

The named tests were mutation-checked by temporarily removing `ConnectionId` parameter metadata from `Test-EntitySyncConnection`, rebuilding, and running:

```text
pwsh -NoProfile -Command "Invoke-Pester -Path Tests/LISSTech.EntitySync.Tests.ps1 -FullNameFilter '*durable control service*' -Output Detailed"
```

Exit 1: 3 passed, 1 failed; the generation-pinned connection test reported missing `ConnectionId` (`artifact://2019`). The attribute was restored before final GREEN.

### Fix-round behavioral RED

The authenticated workbook round-trip test initially failed because direct JSON
deserialization could not bind the immutable durable item constructor
(`artifact://2042`, 8 passed/1 failed). The implementation now uses an explicit
artifact DTO and rehydrates through the domain constructor.

After adding the fail-closed Pester cases, the first named run was 3 passed/2
failed: importing a durable envelope without control configuration attempted
to resolve an ASP.NET implementation assembly at module load, and
`Test-EntitySyncConnection -ConnectionId` fell through to the local registry.
The workbook boundary was changed to the existing
`IEntitySyncDataProtector` port with a dedicated artifact purpose, and the
durable-only parameter is now rejected before local adapter resolution.

### Fix-round 2 atomic import RED

The first repository-owned import tests ran against live PostgreSQL after
Application validation. They failed **2/2** (`artifact://2081`): the new
repository import contract was still unsupported, and disabling the policy
between the Application read and persistence reached the old insert path
instead of producing a typed policy-change rejection. These failures proved
that validation plus a later ordinary insert was not an atomic security
boundary.

### Fix-round 3 durable receipt/locking RED

The final reviewer-requested live-PostgreSQL adversarial suite failed **7/7**
before the repository hardening (`artifact://2114`): import did not share the
ordinary plan-identity lock; replay returned the workbook's stale Draft
snapshot after the persisted plan moved through Approved, Consumed, or
Expired; the import receipt occupied the public HTTP idempotency namespace;
database-clock expiry was not enforced; and no dedicated receipt migration
existed. The blocking-lock test's `TimeoutException` is the expected RED
proof that import completed while an ordinary plan identity lock remained
held.

### Fix-round 4 receipt-first Application replay RED

The live-PostgreSQL lost-response tests failed **3/3**
(`artifact://2141`) before the Application correction. After a committed
import response was deliberately ignored, disabling/versioning the policy and
rotating a connection caused the exact same tenant/caller-key/actor/workbook
retry to fail in `DurablePlanService` with `DurablePlanPolicyChangedException`
or `DurablePlanConnectionChangedException`. The persisted Expired case failed
the same stale policy precheck. This proved mutable Application reads occurred
before the authoritative completed receipt could replay.



## GREEN evidence

- Final focused C# command: **27 passed, 0 failed** (`artifact://2152`). Its **11 parity cases** include all seven MCP/direct-HTTP translations plus authenticated `WebApplicationFactory` requests for create/approve/dry-run/apply through authorization, `IdempotencyEndpointFilter`, and the real endpoint. The pass-through executor supplies a deliberately different internal execution token, so comparison proves the exact caller key—not the receipt token—reaches the shared Application command. The remaining cases cover authenticated workbook rehydration, Application import guards, receipt-first lost-response recovery, and live-PostgreSQL atomic import/reconstruction/locking/replay checks.
- Exact named Pester filter: **5 passed, 0 failed, 192 not run**. Cases prove exactly 16 exports/zero aliases, generation-pinned IDs/durable parameter sets, DPAPI profile locality, fail-closed durable workbook handling, and durable-only parameter rejection before local fallback.
- Minimal builds: `dotnet build src/LISSTech.EntitySync.csproj --no-restore` and `dotnet build mcp/LISSTech.EntitySync.Mcp.csproj --no-restore`: **0 errors** (`artifact://2150`). Existing version-conflict and nullable warnings remain.

## PostgreSQL restart/provider reconstruction

An isolated PostgreSQL 18 instance was exposed on port 5433. Command:

```text
DATABASE_URL='Host=127.0.0.1;Port=5433;Database=postgres;Username=entitysync;Password=entitysync;Pooling=false' dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter 'FullyQualifiedName~Connection_and_policy_repositories_are_tenant_isolated_and_lossless|FullyQualifiedName~Manifest_round_trips_and_item_failure_rolls_back_plan|FullyQualifiedName~Operation_graph_and_transitions_enforce_queue_identity_and_terminal_consistency' --no-restore
```

**3 passed, 0 failed** (`artifact://2069`). Connection, plan/manifest, and operation item/status reads deliberately use newly constructed PostgreSQL repository instances and return identical durable IDs/state. No static repository participates.

The fix-round 2 live-PostgreSQL import suite also passed **3/3**
(`artifact://2089`). A completed caller-key receipt replayed the identical
plan through a newly constructed repository, while the same key with either a
different plan or actor conflicted. Two deterministic races paused after the
Application reads, then respectively disabled the policy and rotated a
connection generation. Persistence rejected both with the typed result and
left neither a plan nor an idempotency receipt.

The fix-round 3 live-PostgreSQL suite passed **11/11**
(`artifact://2126`). It proves import blocks behind the exact ordinary
plan-identity advisory lock, durable receipts cannot collide with either raw
or `plan.import:`-prefixed HTTP idempotency keys, replay reloads the current
persisted Approved/Consumed/Expired plan status, database-clock-expired
artifacts insert neither plan nor receipt, policy and connection mutations
cannot race the transaction, and the new migration is idempotent. A separate
focused check passed **2/2** (`artifact://2124`) for fail-closed receipt/plan
digest mismatch and the receipt foreign key's `ON DELETE CASCADE` retention
contract.

The fix-round 4 lost-response and in-memory parity filter passed **5/5**
(`artifact://2143`), and all durable import tests passed **15/15**
(`artifact://2145`). Exact completed-receipt retries now return the current
persisted Draft or Expired plan ID/status/digest even after policy disable and
connection rotation. A different caller key after those mutations is a new
request and returns the repository's typed PolicyChanged or
ConnectionChanged result. The test repository enforces the same
receipt-first/new-request boundary rather than relying on an Application
precheck.

The existing `Lost_response_crash_restart_and_checkpoint_failure_never_redispatch` test was run twice and failed at the same checkpoint-delay assertion (`artifact://2012`, `artifact://2023`). Task 10 did not change `VendorOutcomeReconciler`, `EntitySyncOperationWorker`, or this test method; `git diff` confirms the only fixture changes replace removed MCP coordinator wiring with the shared facade, and the failing test never invokes that facade (`artifact://2025`). This is therefore an independently exposed existing worker/reconciler issue, not hidden or claimed GREEN.

## PowerShell behavior/locality matrix

| Path | Durable behavior | PowerShell-only/local behavior |
|---|---|---|
| Connect/Get connection | Creates/rotates or lists server-managed definitions; emits safe projection without ciphertext | Explicit profile is ephemeral DPAPI/adapter state |
| Test/entity/lookup/SuiteQL/custom-property | `-ConnectionId` acquires current durable generation from `IConnectionRuntimeFactory` | No ID uses local lease and inserts no durable definition |
| `New-EntitySyncPlan` | `-PolicyId` + `-IdempotencyKey` creates immutable durable plan | Existing object/pipeline planning stays local without durable control |
| `Invoke-EntitySyncPlan` | `-PlanId` queues dry-run; apply requires one-time `-ApprovalId`; returns immediately | Reviewed object execution remains local |
| `Invoke-EntitySyncChain` | Queues one operation per durable plan; positional approvals are apply-only | Existing workbook chain remains local |
| Export | Pages complete durable manifest by `PlanId` | Workbook/file transport remains PowerShell-only |
| Import | Validates complete artifact/digest, then inserts immutable manifest | Legacy workbook/JSON reading remains local without durable control |
| Profile cmdlets | Never enter server composition/create HTTP state | DPAPI create/list/remove/default remains local |

## Workbook durability

`PowerShellDurablePlanWorkbook` stores a versioned, authenticated durable-manifest payload in the workbook package. Protection goes through `IEntitySyncDataProtector` with a purpose isolated from connection secrets and audit values; the protected payload contains the tenant and plan identity, and import verifies those authenticated values against the envelope and active control tenant. The Application boundary performs only immutable container/entry/manifest digest, item-count, tenant/identity, and Draft validation. The durable repository first replays an exact completed receipt; only a new receipt validates the latest enabled policy version/hash/route/connection binding, both connection generations, and database-clock expiry. Exact replay returns the current persisted plan; same caller key with a different plan or actor conflicts. Export reconstructs the full manifest and persisted status from bounded durable pages. File transport and DPAPI remain PowerShell-only.

Import idempotency is now repository-owned and atomic with plan/item
persistence. A dedicated `entitysync.plan_import_receipts` table isolates
import caller keys from the public HTTP endpoint namespace and retains
completed receipts independently of plan expiry; its plan foreign key uses
`ON DELETE CASCADE` so eventual physical plan retention cleanup cannot be
blocked. The canonical request hash binds tenant, plan ID, plan digest, and
actor beneath the exact caller key. One transaction acquires the same plan
identity advisory lock used by ordinary creation, reads the database clock,
then serializes the import caller-key receipt. A completed exact receipt is
the first mutable-state decision: replay reloads the current persisted plan
and verifies its stored digest without consulting current policy, generation,
or expiry state and without trusting the workbook snapshot. Only a new
request acquires the policy identity advisory lock, rechecks the latest
enabled policy version/hash/route/source/target bindings, locks both enabled
connection generations `FOR SHARE`, and writes the immutable plan/items plus
permanent receipt. Failure rolls back both plan state and receipt;
database-expired new artifacts return the typed Expired result before either
is inserted.

## Authority removal and changed areas

Removed production `src/Runtime/InMemoryEntitySyncPlanRepository.cs` and old `mcp/EntitySyncApplyCoordinator.cs` after migration. `EntitySyncPlanner` no longer writes a hidden legacy store. The test-only repository moved to the platform test assembly. Hosting registers PostgreSQL durable repositories and scoped shared commands, not production `IEntitySyncPlanRepository`.

Changed areas: Application facade/planner/hosting; MCP connection/sync/exclusion tools and HTTP translators/endpoints; PowerShell durable runtime/workbook helpers and affected connection/read/write/plan/import/export cmdlets; test fixtures/parity/Pester/reconstruction; durable lifecycle help for New/Invoke/Chain/Export/Import.

## Security self-review

- Tenant/actor originates in authenticated transport contexts and stays explicit at the Application boundary.
- Tenant scoping, generation fencing, idempotency, digest/inspection coverage, approval expiry and one-time consumption, and exclusion revisions remain in Application/repositories.
- Apply only queues; no vendor work is awaited by remote surfaces.
- PowerShell connection output uses `EntitySyncControlConnectionInfo`; ciphertext is not emitted.
- Adapter configuration is server-managed; MCP/HTTP accept no raw endpoints or credentials.
- DPAPI profiles are absent from server DI and explicit profiles bypass durable mutation.
- Workbook authentication, tenant/plan identity, domain digest, item count, and Draft artifact status are immutable validations at the Application boundary; mutable policy, connection-generation, and database-clock expiry decisions occur only in the atomic repository for a new receipt.
- Completed receipt replay precedes all mutable checks, so a lost response cannot become unrecoverable after policy disable, connection rotation, or persisted plan expiry; new caller keys remain fenced by those current-state checks.
- Import receipts are durable and non-expiring while their immutable plan exists, isolated from HTTP receipt cleanup, actor/request bound, and fail closed if their referenced plan is missing or digest-mismatched.
- No synchronous I/O was added to MCP/HTTP/Application. PowerShell waits only at the synchronous cmdlet boundary over async services.

## Concerns

1. The repeatable worker/reconciler assertion failure described above remains actionable outside Task 10; direct connection/plan/run reconstruction is GREEN.
2. Minimal builds retain existing MSB3277 and nullable warnings, with no errors.
3. Named Pester does not perform a successful live PowerShell/PostgreSQL import/export round-trip. Authenticated workbook success/tamper/key isolation/status preservation and Application import replay/conflict/policy/generation validation are covered in the focused C# run; durable paging and live PostgreSQL repository reconstruction are covered separately.
