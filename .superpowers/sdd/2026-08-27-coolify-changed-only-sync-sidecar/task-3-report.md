# Task 3 Report: Changed-Linked-Only Planning

## Status

Complete. Every Task 3 brief checkbox is implemented. Work is limited to the planner, its direct test constructor callsites, the focused planning tests, and this requested report. No persistence, migration, apply, scheduler, hosting, or deployment implementation was changed.

## Changed Files

- `src/Application/EntitySyncPlanner.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ChangedOnlyPlanningTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/PlatformTests.cs` (constructor helper only)
- `Tests/LISSTech.EntitySync.Platform.Tests/EntitySyncApplyCoordinatorTests.cs` (constructor helper only)
- `.superpowers/sdd/2026-08-27-coolify-changed-only-sync-sidecar/task-3-report.md` (this report)

## Design Decisions

- `EntitySyncPlanner` now receives the same `IEntityMapper` contract used by apply plus `IEntitySyncChangeStateRepository`. Direct test callsites use `DefaultEntityMapper` and `InMemoryEntitySyncChangeStateRepository`.
- Changed-only scope validation happens in request validation, before connection or repository access. The accepted form is exactly 64 lowercase hexadecimal characters; null, empty, whitespace, short, non-hex, and uppercase values are rejected.
- The planner builds `EntitySyncChangeStateRoute` from the normalized vendors, resolved connection IDs, and resolved entity types. It performs one `GetBySourceIdsAsync` call containing all bounded source IDs before item finalization.
- `EntitySyncPlanExecution` records the requested update policy and change-state scope. Standard requests retain their existing default `Standard`/null values.
- Standard item planning continues through the existing `CreateItem` result without changed-only mapping, hashing, state lookup, or policy mutation.
- Changed-only policy is applied only to normal matched items. Anything other than an `Update`/`Linked` item with a target becomes `None` and receives the recurring-policy explanation; persistent exclusions were already non-executable and remain unchanged.
- Eligible linked updates are mapped with the injected mapper and the plan's exact `MatchOptions`. `EntityWriteRequestDigest.Compute` supplies the desired hash and schema version.
- A checkpoint suppresses the write only when source lookup succeeds and target ID (ordinal-ignore-case), hash version, and payload hash (ordinal) all match. Suppressed items become `None`/`Unchanged` while retaining the desired hash metadata.

## TDD Evidence

### Red

The focused tests were created before changing the planner constructor or behavior.

Command:

```bash
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --filter FullyQualifiedName~ChangedOnlyPlanningTests --no-restore
```

Exit code: `1`

Expected missing-constructor failure:

```text
/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/ChangedOnlyPlanningTests.cs(260,21): error CS1729: 'EntitySyncPlanner' does not contain a constructor that takes 6 arguments [/Users/mwisniowski/Projects/LISSConsulting/LISSTech.EntitySync/.worktrees/coolify-changed-only-sync-sidecar/Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj]
```

### Green

The final focused class covers first-run linked updates and exact mapped digests, identical checkpoints, a mapped-field change, target-ID change, hash-version mismatch, high-confidence name-only and ambiguous matches, missing targets, unmatched sources, six missing/invalid scope cases, one batched state read, execution policy/scope metadata, and preserved Standard behavior.

Command:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --no-restore --filter FullyQualifiedName~ChangedOnlyPlanningTests
```

Exit code: `0`

Exact output:

```text
Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 87 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

## Focused Regression Evidence

Command:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --no-restore --filter "FullyQualifiedName~ChangedOnlyPlanningTests|FullyQualifiedName~PlanningRejectsUnboundedEntitySets|FullyQualifiedName~McpFocusedPlanUsesBoundedQueryAndExactSourceId|FullyQualifiedName~FocusedPlanRejectsSourceIdOutsideBoundedQueryBeforeReadingTargets|FullyQualifiedName~CreateMissingMarksPersistentlyExcludedSourcesAsNonExecutable|FullyQualifiedName~EmptyExclusionPolicyAllowsCreateMissing|FullyQualifiedName~CreateMissingFailsClosedWhenExclusionsCannotBeRead|FullyQualifiedName~ApplyFailsClosedWhenExclusionsCannotBeRevalidated|FullyQualifiedName~ApplyRejectsAPlanWhenSourceWasExcludedAfterPlanning|FullyQualifiedName~ApplyRejectsConnectionReplacedAfterPlanning|FullyQualifiedName~ApplyKeepsUsingPinnedConnectionWhenItIsReplacedDuringWrite"
```

Exit code: `0`

Exact output:

```text
Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27, Duration: 89 ms - LISSTech.EntitySync.Platform.Tests.dll (net8.0)
```

No broad validation, formatter, linter, or project-wide test suite was run.

## Self-Review

- Re-read the Task 3 brief and checked every checkbox against the final implementation and tests.
- Confirmed the changed-only state repository is called exactly once with every source ID and the complete resolved route, and is not called for Standard plans or invalid changed-only scopes.
- Confirmed mapper output—not source/target entity serialization—is the digest input, so mapped scalar/field changes, target IDs, schema version, and scope remain covered by the established digest/plan contracts.
- Confirmed only a persistently linked `Update` can remain executable in changed-only mode; name-only links, reviews, ambiguity, create-missing candidates, missing targets, and unmatched sources are all non-executable.
- Confirmed exact checkpoint equality produces `None`/`Unchanged`, while mapped payload, target ID, or hash-version changes preserve `Update`/`Linked`.
- Confirmed Standard mode avoids changed-only repository reads, mapping, digest computation, and action mutation; the focused Standard regression still produces the pre-existing high-confidence `Link`.
- Confirmed all direct planner constructor callsites use the new dependencies and reuse the same mapper instance between planning and service apply within each helper.
- Confirmed persistent exclusion flow remains untouched and changed-only transformation occurs before `plans.Add`.
- Confirmed no out-of-scope persistence, migration, apply, scheduler, hosting, deployment, or project configuration changes were included.

## Commits

- `bea082d006d7a4b6d878b6af2a4d26e2eb3dd808` — `feat: plan changed linked updates only`

## Concerns

No identified correctness concern. The workstation only has the .NET 10 runtime while the test target is `net8.0`, so focused test execution required the repository plan's prescribed `DOTNET_ROLL_FORWARD=Major`. The initial implementation commit was made unsigned because the configured 1Password SSH signer failed to fill its buffer in this non-interactive worker session; repository contents and test evidence are unaffected.
