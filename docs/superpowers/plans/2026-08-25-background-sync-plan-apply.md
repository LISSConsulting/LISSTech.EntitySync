# Background Sync Plan Apply Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make approved MCP sync-plan applies start exactly once, survive initiating-request cancellation, and expose pollable aggregate progress and terminal results.

**Architecture:** A singleton MCP apply coordinator registers one in-memory operation per tenant and plan, starts `EntitySyncService.ApplyAsync` with the application-stopping token rather than the request token, and stores immutable progress snapshots. The application service reports per-item aggregate progress while retaining domain validation and plan state transitions; MCP exposes immediate start and read-only polling tools.

**Tech Stack:** .NET 8, C# 12, ASP.NET Core hosted lifetime, ModelContextProtocol server tools, xUnit 2.9.

## Global Constraints

- Background execution must survive MCP client disconnects but not server process restarts.
- No automatic retry after any vendor write may have started.
- Preserve approval digest, permanent-exclusion, connection-generation, and single-consumption safeguards.
- Keep `apply=false` synchronous and read-only.
- Never expose credentials, raw vendor responses, or every successful item through polling.
- Follow TDD: run each named regression test red before production changes and green afterward.

---

### Task 1: Application Progress Contract

**Files:**
- Create: `src/Application/EntitySyncApplyProgress.cs`
- Modify: `src/Application/EntitySyncService.cs:48-154`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs:61-146`

**Interfaces:**
- Produces: `EntitySyncApplyProgress(int Total, int Processed, int Succeeded, int Failed, int Skipped, EntitySyncApplyItemResult Item)`.
- Produces: `EntitySyncService.ApplyAsync(string tenantId, string planId, bool apply, CancellationToken cancellationToken, Action<EntitySyncApplyProgress>? reportProgress = null)`.
- Preserves: all existing four-argument `ApplyAsync` callers.

- [ ] **Step 1: Write the failing progress test**

Add a test that creates two source entities, applies the approved plan, collects progress synchronously, and asserts snapshots after both completed items:

```csharp
[Fact]
public async Task ApplyReportsAggregateProgressAfterEveryProcessedItem()
{
    using var connections = new InMemoryEntityConnectionRepository();
    connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme"), Source("2", "Beta")]));
    connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
    var service = CreateService(connections);
    var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
    InspectAllAndApprove(service, plan);
    var progress = new List<EntitySyncApplyProgress>();

    var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None, progress.Add);

    Assert.True(result.Success);
    Assert.Equal([1, 2], progress.Select(item => item.Processed));
    Assert.All(progress, item => Assert.Equal(2, item.Total));
    Assert.Equal(2, progress[^1].Succeeded);
    Assert.Equal(0, progress[^1].Failed);
    Assert.Equal(0, progress[^1].Skipped);
}
```

Add `InspectAllAndApprove` to inspect every page at page size 100 before approval.

- [ ] **Step 2: Run the focused test and verify red**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~ApplyReportsAggregateProgressAfterEveryProcessedItem
```

Expected: compilation fails because `EntitySyncApplyProgress` and the five-argument `ApplyAsync` overload do not exist.

- [ ] **Step 3: Add the immutable progress record**

Create:

```csharp
namespace LISSTech.EntitySync.Application;

public sealed record EntitySyncApplyProgress(
    int Total,
    int Processed,
    int Succeeded,
    int Failed,
    int Skipped,
    EntitySyncApplyItemResult Item);
```

- [ ] **Step 4: Report progress without rescanning results**

Extend `ApplyAsync` with the optional callback. Maintain integer counters while iterating. After each skipped, dry-run, successful, or failed item is appended, update the relevant counter and invoke:

```csharp
reportProgress?.Invoke(new EntitySyncApplyProgress(
    plan.Items.Count,
    results.Count,
    succeeded,
    failed,
    skipped,
    results[^1]));
```

Return the maintained counters in `EntitySyncApplyResult`; do not repeatedly count the growing list.

- [ ] **Step 5: Run the focused and existing service apply tests**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter "FullyQualifiedName~ApplyReportsAggregateProgressAfterEveryProcessedItem|FullyQualifiedName~ApprovedPlanIsAppliedOnlyOnce|FullyQualifiedName~CancelledApplyMovesPlanToFailedTerminalState"
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit the application contract**

```bash
git add src/Application/EntitySyncApplyProgress.cs src/Application/EntitySyncService.cs Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs
git commit -m "feat: report sync plan apply progress"
```

---

### Task 2: Single-Start Background Coordinator

**Files:**
- Create: `mcp/EntitySyncApplyCoordinator.cs`
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs`

**Interfaces:**
- Consumes: `EntitySyncService.ApplyAsync(..., Action<EntitySyncApplyProgress>? reportProgress)` from Task 1.
- Consumes: `IEntitySyncPlanRepository.Get(string tenantId, string planId)` and `IHostApplicationLifetime.ApplicationStopping`.
- Produces: `EntitySyncApplySnapshot` with `PlanId`, `Status`, `Total`, `Processed`, `Succeeded`, `Failed`, `Skipped`, `StartedAt`, `CompletedAt`, `Failures`, and `Error`.
- Produces: `EntitySyncApplyCoordinator.Start(string tenantId, string planId)` and `Get(string tenantId, string planId)`.

- [ ] **Step 1: Write failing coordinator disconnect and duplicate-start tests**

Use a blocking target adapter whose create waits on a `TaskCompletionSource`. Build and approve a one-item plan, call `Start`, cancel an unrelated request token, call `Start` again, release the write, and poll with bounded condition-based waiting. Assert:

```csharp
Assert.Equal("Applying", first.Status);
Assert.Equal(first.StartedAt, repeated.StartedAt);
Assert.Equal(1, target.CreateCalls);
Assert.Equal("Applied", terminal.Status);
Assert.Equal(1, terminal.Processed);
Assert.Equal(1, terminal.Succeeded);
```

Add a concurrent-start test using `Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => coordinator.Start("tenant", plan.Id))))` and assert one target write.

- [ ] **Step 2: Run coordinator tests and verify red**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncApplyCoordinatorTests
```

Expected: compilation fails because the coordinator and snapshot types do not exist.

- [ ] **Step 3: Implement immutable snapshots and atomic registration**

Create `EntitySyncApplyCoordinator` as a singleton-safe sealed class. Normalize its dictionary key as `tenantId.Trim() + "\n" + planId.Trim()`. Register a candidate operation with `ConcurrentDictionary.GetOrAdd` before assigning its observed task; only the winning candidate calls `RunAsync`.

`Start` must:

```csharp
var key = Key(tenantId, planId);
if (operations.TryGetValue(key, out var existing)) return existing.Snapshot;
var plan = plans.Get(tenantId, planId);
if (!plan.Status.Equals(EntitySyncPlanStatuses.Approved, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Plan must be approved before apply.");
var candidate = new ApplyOperation(plan.Id, plan.Items.Count, timeProvider.GetUtcNow());
var operation = operations.GetOrAdd(key, candidate);
if (ReferenceEquals(operation, candidate)) operation.Start(RunAsync(tenantId, plan.Id, operation));
return operation.Snapshot;
```

If an operation already exists, return it before checking current plan status so retries after a lost response remain idempotent.

- [ ] **Step 4: Run with application-owned cancellation and aggregate failures**

`RunAsync` must call the service with `applicationLifetime.ApplicationStopping`. Replace the operation snapshot under a private lock for every progress callback. Append only failed, non-skipped item summaries to `Failures`. On success, publish `Applied`; on cancellation or exception, publish `Failed`, preserve counters, set a safe `Error`, and observe the task exception.

- [ ] **Step 5: Add shutdown partial-progress test**

Use a fake `IHostApplicationLifetime`, let the first of two writes finish, cancel `ApplicationStopping` while the second blocks, then assert terminal `Failed`, `Processed == 1`, and no retry starts from a repeated `Start` call.

- [ ] **Step 6: Run all coordinator tests**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~EntitySyncApplyCoordinatorTests
```

Expected: all coordinator tests pass without sleeps longer than the bounded poll timeout.

- [ ] **Step 7: Commit the coordinator**

```bash
git add mcp/EntitySyncApplyCoordinator.cs Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs
git commit -m "feat: run sync plan applies in background"
```

---

### Task 3: MCP Start and Poll Tools

**Files:**
- Modify: `mcp/SyncTools.cs:136-180`
- Modify: `mcp/Program.cs:168-184`
- Modify: `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs:418-424`
- Modify: `mcp/README.md:60-69`

**Interfaces:**
- Consumes: `EntitySyncApplyCoordinator.Start` and `Get` from Task 2.
- Produces MCP tool: `apply_sync_plan` returns immediately for `apply=true`.
- Produces MCP tool: `get_sync_plan_apply(planId)` performs read-only polling.

- [ ] **Step 1: Write failing MCP contract tests**

Extend the reflection test:

```csharp
Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.GetSyncPlanApply)));
```

Add a tool-surface test that starts a blocked apply, verifies the JSON response has `success: true` and `status: "Applying"`, cancels the initiating token, releases the write, calls `GetSyncPlanApply`, and verifies `status: "Applied"` and `succeeded: 1`.

- [ ] **Step 2: Run MCP tests and verify red**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter "FullyQualifiedName~McpExposesInspectApproveAndApplyWorkflow|FullyQualifiedName~McpApplyStartsAndPollsAfterRequestCancellation"
```

Expected: compilation fails because `GetSyncPlanApply` does not exist and `ApplySyncPlan` still awaits the request token.

- [ ] **Step 3: Change `apply_sync_plan` start behavior**

Inject `EntitySyncApplyCoordinator` into `ApplySyncPlan`. For `apply=false`, keep awaiting `service.ApplyAsync` with the request token. For `apply=true`, call `coordinator.Start(context.TenantId, planId)` synchronously and serialize `{ success = true, snapshot }`. Do not pass the request token into background execution.

- [ ] **Step 4: Add the read-only polling tool**

Add:

```csharp
[McpServerTool]
[Description("Get aggregate progress and terminal status for a started sync-plan apply.")]
public static string GetSyncPlanApply(
    EntitySyncApplyCoordinator coordinator,
    McpRequestContext context,
    [Description("Plan ID returned from create_sync_plan")] string planId)
```

Serialize the current snapshot. Map missing operations and safe state errors to non-secret JSON errors.

- [ ] **Step 5: Register the coordinator and update operator docs**

Register `EntitySyncApplyCoordinator` as a singleton in `AddEntitySyncPlatform`. Update the safe workflow so step 6 starts background execution and step 7 polls `get_sync_plan_apply` until `Applied` or `Failed`. State that repeated starts do not retry or duplicate writes.

- [ ] **Step 6: Run MCP and service regression tests**

Run:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj
```

Expected: all platform tests pass.

- [ ] **Step 7: Commit the MCP contract**

```bash
git add mcp/SyncTools.cs mcp/Program.cs mcp/README.md Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs
git commit -m "feat: expose sync plan apply polling"
```

---

### Task 4: Full Verification and Deployment

**Files:**
- Verify only; no planned source edits.

**Interfaces:**
- Consumes: completed MCP start/poll contract.
- Produces: pushed commits, deployed container, healthy endpoint, and exercised apply polling path.

- [ ] **Step 1: Run repository verification**

Run:

```bash
just build
just test
just mcp-build
just mcp-compose-config
```

Expected: every command exits 0.

- [ ] **Step 2: Build the production container**

Run:

```bash
just mcp-docker-build
```

Expected: the `entitysync-mcp` production image builds successfully.

- [ ] **Step 3: Push the implementation commits**

Run:

```bash
git push origin main
```

Expected: `origin/main` advances through the design, plan, and implementation commits.

- [ ] **Step 4: Deploy through the configured Coolify Git resource**

Use the repository's configured deployment integration after the push. Confirm the deployed revision matches local `HEAD`; do not print secrets or environment values.

- [ ] **Step 5: Verify deployed health and MCP behavior**

Confirm `/health` returns `{"status":"healthy"}`. Reconnect NetSuite and HaloPSA after the restart, create and inspect a focused one-entity plan, approve it, start `apply_sync_plan`, and poll `get_sync_plan_apply` to terminal `Applied`. Verify the created or updated target entity once and confirm a repeated start returns the same terminal snapshot without another write.
