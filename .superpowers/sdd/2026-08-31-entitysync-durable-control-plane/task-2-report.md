# Task 2 Implementation Report

## Status

DONE

## Commit

`feat(control): define durable synchronization aggregates`

The containing commit SHA is reported in the Task 2 terminal result. A Git commit cannot embed its own object ID in content that participates in that object ID.

Prerequisite schema commit: `903922562aa8bdf42ac653e7b5c89551b6daff6b`.

## Changed Files

- `src/Core/EntitySyncConnectionDefinition.cs`
- `src/Core/EntitySyncPolicy.cs`
- `src/Core/EntitySyncDurablePlan.cs`
- `src/Core/EntitySyncOperation.cs`
- `src/Core/EntitySyncSchedule.cs`
- `src/Core/EntitySyncAuditEvent.cs`
- `src/Ports/IConnectionDefinitionRepository.cs`
- `src/Ports/ISyncPolicyRepository.cs`
- `src/Ports/IDurableSyncPlanRepository.cs`
- `src/Ports/ISyncOperationRepository.cs`
- `src/Ports/ISyncScheduleRepository.cs`
- `src/Ports/ISyncAuditRepository.cs`
- `src/Ports/IIdempotencyRepository.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlModelTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-2-report.md`

## RED

Command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~ControlModelTests
```

The initial focused run failed at compile time with the expected absent-type errors, including:

```text
error CS0246: The type or namespace name 'EntitySyncConnectionDefinition' could not be found
error CS0246: The type or namespace name 'EntitySyncPolicyDefinition' could not be found
error CS0246: The type or namespace name 'EntitySyncDurablePlan' could not be found
error CS0246: The type or namespace name 'EntitySyncDurablePlanItem' could not be found
error CS0246: The type or namespace name 'EntitySyncInspectionSession' could not be found
```

Review-driven RED runs also proved that approval consumption initially lacked the full Apply operation/items needed for an atomic insert, and that selection bounds initially rejected a supported bounded search plus exact source-ID assertion.

## GREEN

The same exact focused command passed after the prerequisite schema fix and final contract alignment:

```text
Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16, Duration: 102 ms
```

No formatter, linter, Pester, or project-wide test suite was run.

## Key Decisions

- PostgreSQL migrations 004-006 remain authoritative. Every durable row has a tenant-bearing immutable Core representation, including encrypted-retention rows without plaintext sensitive-value properties.
- SHA-256 values normalize to lowercase and reject non-64-character hexadecimal input.
- Connection generations and policy/schedule versions are positive; next-version methods preserve stable identity and increment exactly once.
- Policy definitions validate score bounds and ordering, disjoint case-insensitive allowed/blocked fields, connection identities, and immutable field sets.
- Durable plans bind policy ID/version/hash, connection IDs/generations, source search/count/exact-ID bounds, digest, item count, actors, and manifest timestamps. Item count is derived from immutable contiguous item ordinals; the policy hash is derived through the immutable policy foreign key.
- Durable plan items copy ordered match reasons and field diffs. Field names are unique case-insensitively, matching the final migration validators.
- Inspection persistence is explicitly session-based: open a digest/generation-bound session, append immutable child ranges, complete exact coverage once, query completion, approve that completed session, and consume the approval once.
- Approval consumption accepts the full Apply operation and operation-item set so Task 3 can insert them and advance the plan in one transaction. The schema's tenant-scoped unique Apply-approval index supplies single-use enforcement.
- Apply operations cannot be constructed without an approval. Plan, inspection, and operation transition helpers reject illegal state changes.
- All seven repository ports are tenant-first and asynchronous. No persistence implementation or synchronous production database method was added.
- The initially missing plan selection and review-evidence persistence was resolved by prerequisite commit `903922562aa8bdf42ac653e7b5c89551b6daff6b`: plan connection IDs and selection fields, plus item score/type/ordered reasons/ordered diffs, now map directly to migration 005.

## Self-Review

- Every migration 004-006 table is represented: connection definitions, policies, plans/items, inspection sessions/ranges, approvals, idempotency receipts, operations/items/snapshots, schedules, canonical changes, audit events, and encrypted audit values.
- Connection, policy, digest, generation, inspection, approval, and operation identities are retained across public models and tenant-first repository calls.
- Read-only sets and lists are defensive copies; caller mutation cannot change policy fields, match reasons, field diffs, plan pages, or audit pages.
- The exact database state names are covered by focused tests.
- Count-only source bounds and a bounded search with an exact immutable-ID assertion are both supported.
- A final independent review found no remaining Critical or Important Task 3 round-trip blocker against the finalized migration 005.
- `git diff --cached --check` is run immediately before commit.

## Concerns

None.

## Fix Round 1

### Status

DONE

### Parent Commit

`d52f46c46f75ee3d7518a53c3ca27a69157ffb1e` — `feat(control): define durable synchronization aggregates`

The fix commit SHA is returned in the terminal result because a commit cannot embed its own object ID.

### Changed Files

- `src/Core/EntitySyncCanonicalDigest.cs`
- `src/Core/EntitySyncDurablePlan.cs`
- `src/Core/EntitySyncOperation.cs`
- `src/Application/EntitySyncPlanDigest.cs`
- `src/Ports/IDurableSyncPlanRepository.cs`
- `src/Ports/ISyncOperationRepository.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlModelTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-2-report.md`

### RED

Command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~ControlModelTests
```

The focused compile failed for the intentionally absent manifest, hydration, and expiration contracts:

```text
CS0103: The name 'EntitySyncDurablePlanManifest' does not exist in the current context
CS0117: 'EntitySyncOperation' does not contain a definition for 'Rehydrate'
CS0117: 'EntitySyncOperationItem' does not contain a definition for 'Rehydrate'
CS0117: 'IDurableSyncPlanRepository' does not contain a definition for 'TryExpireAsync'
```

### GREEN

The same focused command passed after the fixes:

```text
Passed! - Failed: 0, Passed: 20, Skipped: 0, Total: 20, Duration: 104 ms
```

### Key Decisions

- `EntitySyncDurablePlanManifest.Create` defensively copies items, rejects cross-tenant/cross-plan items and noncontiguous ordinals, derives item count, and computes the digest from immutable plan metadata plus the exact ordered item manifest.
- Durable plan insertion now accepts only the sealed manifest, removing independently caller-controlled item count, digest, and item-list inputs.
- The existing `EntitySyncPlanDigest` and the durable manifest both use the shared `EntitySyncCanonicalDigest` serialization/SHA-256 primitive. The legacy plan projection is unchanged, so no parallel hashing convention was introduced.
- Approval consumption includes transaction time together with the exact approval/inspection/plan/digest/generation identity and full Apply operation/items; Task 3 must check approval expiry in that same transaction.
- Operation item compare-and-set includes expected operation attempt, lease owner, transaction time, and expected item outcome, allowing stale or expired workers to return `false`.
- Plan expiration is an exact tenant/plan/digest/expected-status compare-and-set at transaction time. A single-plan method is inherently bounded to one row.
- `EntitySyncOperation.Rehydrate` and `EntitySyncOperationItem.Rehydrate` preserve every migration-005-valid stored value without trimming vendor, route, request, identity, error, lease, or timestamp state. Queue and transition construction retains the stricter operational invariants.
- No migration, migration test, repository implementation, service, HTTP/MCP, PowerShell, formatter, linter, Pester, or project-wide suite was changed or run.

### Concerns

None.

## Fix Round 2

### Status

DONE

### Parent Commit

`db1333feba9b3274cff5fa88e8768876f696ded6` — `fix(control): harden durable operation contracts`

The fix commit SHA is returned in the terminal result because a commit cannot embed its own object ID.

### Changed Files

- `src/Core/EntitySyncDurablePlan.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlModelTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-2-report.md`

### RED

The exact focused command produced the two intended manifest-boundary failures:

```text
Durable_manifest_rejects_non_draft_initial_plan_state:
Assert.Throws() Failure: No exception was thrown

Durable_manifest_rejects_duplicate_item_ids_before_persistence:
Assert.Throws() Failure: No exception was thrown

Failed: 2, Passed: 20, Skipped: 0, Total: 22
```

### GREEN

The same exact focused command passed after the minimal guards:

```text
Passed! - Failed: 0, Passed: 22, Skipped: 0, Total: 22, Duration: 107 ms
```

### Key Decisions

- New durable manifests require exactly `Draft` status, so initial insertion cannot bypass approval/consumption transitions.
- Manifest item IDs are checked with a single bounded `HashSet<Guid>` pass while ordinals and ownership are validated. Duplicate IDs fail before reaching the migration-005 tenant/plan/item primary key.
- No migration or production repository implementation was changed.
- No formatter, linter, Pester, or broad test suite was run.

### Concerns

None.
