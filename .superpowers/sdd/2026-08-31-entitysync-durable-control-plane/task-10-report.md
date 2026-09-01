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

## GREEN evidence

- Exact C# parity filter: **7 passed, 0 failed** (`artifact://2010`). Recording compares operation, tenant, resource ID, page bounds, digest/idempotency/approval input, and actor across MCP and HTTP.
- Exact named Pester filter: **4 passed, 0 failed, 192 not run**. Cases prove exactly 16 exports/zero aliases, generation-pinned IDs/durable parameter sets, DPAPI profile locality, and full-artifact rejection before durable insert.
- Minimal builds: `dotnet build src/LISSTech.EntitySync.csproj --no-restore` and `dotnet build mcp/LISSTech.EntitySync.Mcp.csproj --no-restore`: **0 errors** (`artifact://2016`). Existing version-conflict and nullable warnings remain.

## PostgreSQL restart/provider reconstruction

An isolated PostgreSQL 18 instance was exposed on port 5433. Command:

```text
DATABASE_URL='Host=127.0.0.1;Port=5433;Database=postgres;Username=entitysync;Password=entitysync;Pooling=false' dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter 'FullyQualifiedName~Connection_and_policy_repositories_are_tenant_isolated_and_lossless|FullyQualifiedName~Manifest_round_trips_and_item_failure_rolls_back_plan|FullyQualifiedName~Operation_graph_and_transitions_enforce_queue_identity_and_terminal_consistency' --no-restore
```

**3 passed, 0 failed** (`artifact://2014`). Connection, plan/manifest, and operation item/status reads deliberately use newly constructed PostgreSQL repository instances and return identical durable IDs/state. No static repository participates.

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

`PowerShellDurablePlanWorkbook` stores a versioned Base64 durable-manifest payload and SHA-256 in the workbook package. Import checks container, entry, version, payload digest, manifest digest, item count, tenant, and plan identity before insertion. Export reconstructs the full manifest from bounded durable pages. File transport is absent from MCP/HTTP.

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
- Workbook validation precedes insertion; file transport remains PowerShell-only.
- No synchronous I/O was added to MCP/HTTP/Application. PowerShell waits only at the synchronous cmdlet boundary over async services.

## Concerns

1. The repeatable worker/reconciler assertion failure described above remains actionable outside Task 10; direct connection/plan/run reconstruction is GREEN.
2. Minimal builds retain existing MSB3277 and nullable warnings, with no errors.
3. Named Pester validates malformed workbook rejection before database access; it does not perform a successful live PowerShell/PostgreSQL import/export round-trip. Durable paging and repository reconstruction are covered in C#.
