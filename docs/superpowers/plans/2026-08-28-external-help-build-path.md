# External Help Build-Path Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the stale external-help assertion, push the verified repair, and redeploy the full EntitySync Coolify Compose stack.

**Architecture:** Keep `en-US/` as the committed help source and `Build/Module/en-US/` as the only generated module help output. The test derives the generated path from the module manifest it actually imports, preventing future output-path drift without creating a second artifact tree.

**Tech Stack:** PowerShell 7, Pester 6, .NET 8, Git, Docker Compose, Coolify

## Global Constraints

- Change only `Tests/LISSTech.EntitySync.Tests.ps1` for the repair.
- Do not recreate ignored `Module/en-US/` or change build output paths.
- Preserve module commands, vendor behavior, scheduler policy, database state, and synchronization behavior.
- Deploy the configured full Coolify Compose stack without invoking or applying a sync plan.

---

### Task 1: Correct External-Help Artifact Validation

**Files:**
- Modify: `Tests/LISSTech.EntitySync.Tests.ps1:622-642`
- Test: `Tests/LISSTech.EntitySync.Tests.ps1`

**Interfaces:**
- Consumes: `$script:ModulePath`, initialized by `BeforeAll` to the manifest imported by the suite.
- Produces: validation of committed source help and generated help beside the actual module under test.

- [ ] **Step 1: Build the module**

```bash
just build
```

Expected: `Build/Module/en-US/LISSTech.EntitySync.dll-Help.xml` exists.

- [ ] **Step 2: Reproduce the focused failure**

```powershell
$env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = "$PWD/Build/Module/LISSTech.EntitySync.psd1"
$result = Invoke-Pester -Path ./Tests -FullNameFilter '*New-EntitySyncPlan help uses*' -Output Detailed -PassThru
if ($result.FailedCount -eq 0) { throw 'Expected stale Module/en-US assertion to fail before repair.' }
```

Expected: one failure because `Module/en-US/LISSTech.EntitySync.dll-Help.xml` does not exist.

- [ ] **Step 3: Replace the stale path list**

Replace the relative `en-US` and `Module/en-US` loop with:

```powershell
$helpPaths = @(
  (Join-Path $repoRoot 'en-US' 'LISSTech.EntitySync.dll-Help.xml')
  (Join-Path (Split-Path -Parent $script:ModulePath) 'en-US' 'LISSTech.EntitySync.dll-Help.xml')
)
foreach ($helpPath in $helpPaths) {
  Test-Path $helpPath | Should -BeTrue -Because "$helpPath must exist in source or beside the module under test"
  $helpContent = Get-Content -LiteralPath $helpPath -Raw
  $helpContent | Should -Match 'LtacSourceInvalid' -Because "$helpPath must mirror the doc and advertise LtacSourceInvalid for the LTAC site-to-customer example"
  $helpContent | Should -Not -Match 'LtacSiteParentMissing' -Because "$helpPath must not advertise the obsolete LtacSiteParentMissing match type"
}
```

- [ ] **Step 4: Confirm the focused test passes**

```powershell
$env:LISSTECH_ENTITYSYNC_TEST_MODULE_PATH = "$PWD/Build/Module/LISSTech.EntitySync.psd1"
$result = Invoke-Pester -Path ./Tests -FullNameFilter '*New-EntitySyncPlan help uses*' -Output Detailed -PassThru
if ($result.FailedCount -gt 0) { throw "$($result.FailedCount) focused test(s) failed." }
```

Expected: one passed, zero failed.

- [ ] **Step 5: Run repository verification**

```bash
just test
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --no-restore --verbosity minimal
just test-load
```

Expected: the external-help failure is gone. On macOS, report the two Windows-only DPAPI failures exactly if Pester still counts them as failures. Platform tests and module load must pass.

- [ ] **Step 6: Commit the repair**

```bash
git add Tests/LISSTech.EntitySync.Tests.ps1
git commit -m "test: validate built external help path"
```

---

### Task 2: Push and Deploy the Full Stack

**Files:**
- Verify only: `docker-compose.yaml`
- Verify only: `mcp/README.md`

**Interfaces:**
- Consumes: verified `main` commit and the existing Coolify Compose resource.
- Produces: `origin/main` at the repair commit and healthy deployed MCP, scheduler, and PostgreSQL services.

- [ ] **Step 1: Push main**

```bash
git push origin main
```

Expected: `origin/main` advances to local `main` without force.

- [ ] **Step 2: Trigger or observe the full-stack deployment**

Use the configured Coolify Compose resource for this repository. Redeploy the full stack from `origin/main`; do not change secrets, routes, replica count, volumes, or Compose configuration.

- [ ] **Step 3: Verify the deployed revision and containers**

Confirm the application containers use the pushed revision and that PostgreSQL, `entitysync-mcp`, and `entitysync-scheduler` are running and healthy. Confirm MCP `/health` returns `{"status":"healthy"}` and the scheduler's private `/health` returns `{"status":"healthy"}`.

- [ ] **Step 4: Verify application connectivity**

List MCP connections, reconnect the configured NetSuite and HaloPSA connections if the restart cleared in-memory state, and run their connection tests. Inspect the scheduler `/status` response and verify it is bounded and non-failed. Do not approve or apply a sync plan.

- [ ] **Step 5: Record release evidence**

Report the pushed commit SHA, deployed revision, container health, MCP and scheduler health responses, vendor connection-test results, scheduler state, and any unrelated verification limitation.
