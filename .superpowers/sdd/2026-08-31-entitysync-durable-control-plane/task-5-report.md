# Task 5 Implementation Report

## Status

DONE

## Commit

`feat(control): persist inspected digest-bound plans`

The containing commit SHA is returned in the Task 5 terminal result. A Git commit cannot embed its own object ID in content that participates in that object ID.

## RED Evidence

Exact command:

```text
dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter FullyQualifiedName~DurablePlanServiceTests
```

The focused test project failed at compile time before implementation with the intended absent-service errors:

```text
DurablePlanServiceTests.cs(11,31): error CS0246: The type or namespace name 'DurablePlanService' could not be found
DurablePlanServiceTests.cs(12,31): error CS0246: The type or namespace name 'PlanManifestBuilder' could not be found
```

## GREEN Evidence

The same exact focused command passed after implementation:

```text
Passed! - Failed: 0, Passed: 13, Skipped: 0, Total: 13, Duration: 677 ms
```

The focused service tests exercise:

- a real 684-item immutable manifest over seven 100-item pages, including final-page range `[600,684)`;
- repeated, completed-session, and out-of-order page reads with one durable actor/digest inspection session;
- exact union completion and rejection of incomplete, wrong-actor, wrong-digest, expired, policy-rotated, and generation-rotated approvals;
- 1-based page bounds and the maximum page size of 100;
- deterministic plan, item, payload, and manifest digests with sensitivity to desired-value changes;
- immutable source IDs and deterministic source keys/item IDs;
- the exclusion recheck that reclassifies a newly excluded create before persistence;
- allowed-field filtering, blocked-field omission, canonical nested-object ordering, per-field before/desired hashes, and sensitive-value redaction;
- concurrent approval behavior with one approval result, one deterministic conflict, and one audit event.

Individually named PostgreSQL tests used the local non-production development database and passed:

```text
Manifest_round_trips_and_item_failure_rolls_back_plan
Passed: 1, Failed: 0

Inspection_requires_exact_coverage_and_approval_is_single_use_and_expiring
Passed: 1, Failed: 0

Approval_and_audit_commit_or_roll_back_together
Passed: 1, Failed: 0
```

These tests additionally prove exact field-change round trips, idempotent deterministic manifest insertion, append-only approval audit visibility, and transaction rollback of both the plan transition and approval when the audit insert conflicts.

Fix-round RED evidence:

```text
DurablePlanServiceTests.cs: error CS0246: DurablePlanIdempotencyConflictException was absent
DurablePlanServiceTests.cs: error CS0117: CreateDurablePlanRequest.IdempotencyKey was absent
Inspection_completion_uses_the_exact_union_of_overlapping_ranges:
55000: Inspection ranges must exactly cover every plan ordinal without gaps or overlaps
```

Fix-round focused GREEN evidence:

```text
DurablePlanServiceTests: Passed 14, Failed 0
Exclusion_change_wins_a_route_race_with_plan_creation_atomically: Passed 1, Failed 0
Exclusion_change_wins_a_route_race_with_approval_and_rolls_back_audit: Passed 1, Failed 0
Inspection_completion_uses_the_exact_union_of_overlapping_ranges: Passed 1, Failed 0
Migrations_create_control_plane_and_are_idempotent: Passed 1, Failed 0
Combined focused fix verification: Passed 19, Failed 0
ControlModelTests plus manifest round trip: Passed 23, Failed 0
```

The fix round adds caller-stable tenant-scoped idempotency, recursive object/array redaction,
overlap-tolerant exact range union, and PostgreSQL route serialization between exclusion
mutation, plan insertion, and exact-digest approval. The exclusion/approval race also proves
that the approval audit does not survive a rejected state transition.

Post-review remediation addressed all four Important findings:

- migration 008 now reapplies the overlap-aware inspection completion function for databases
  where migration 005 was already recorded; a migration-ledger replay regression passed;
- exclusion guards compare `lower(sync_plan_items.source_entity_id)` so manifests created by
  the prior hashed-key implementation cannot bypass exclusion checks;
- `AcquireCreationAsync` holds a PostgreSQL session advisory lock for the tenant/key-derived
  plan identity across planning and persistence, and a durable creation-claim row binds that
  identity to the exact canonical request SHA-256;
- the request fingerprint includes optional policy-version presence, normalized selection
  bounds, lifetime ticks, actor, tenant, policy ID, and idempotency key. Concurrent identical
  requests exercise one planner call; every changed body conflicts before planner work.

The final combined focused verification passed:

```text
Passed: 45, Failed: 0, Skipped: 0, Total: 45, Duration: 3 s
```

It includes the 14 service tests, Core/Ports contract tests, creation-claim serialization,
legacy-key exclusion races in both directions, atomic approval-audit rollback, overlapping
range union, migration replay, manifest round trip, and the 2,500-item COPY regression.

### Fix Round 2: Creation-lock namespace

The production-composed PostgreSQL regression initially failed after its two-second
cancellation deadline:

```text
Postgres_composed_creation_completes_and_concurrent_retry_plans_once
Failed: TaskCanceledException after 2 s
```

The session creation lease and the nested direct manifest insert had both derived advisory
locks from the same `tenant:plan` key with seed 0 on different connections. The outer lease
therefore waited on itself during `InsertAsync`. Creation leases now use the explicit
`entitysync:durable-plan-creation:v1` namespace with seed 1, while direct manifest insertion
retains its independent transaction-scoped seed-0 lock. Lock order remains creation lease,
then direct plan insertion.

Focused GREEN:

```text
DurablePlanServiceTests plus Postgres_composed_creation_completes_and_concurrent_retry_plans_once
Passed: 15, Failed: 0, Skipped: 0, Total: 15, Duration: 1 s
```

The production-composed test proves the plan commits and is retrievable before its short
deadline, and that a concurrent identical retry returns the same plan after one planner read.

### Fix Round 3: Non-pinning durable creation claims

The expanded production-composed test used a PostgreSQL pool with `Maximum Pool Size=2`,
blocked the owner in the source adapter, and started eight identical retries. The old
session-lock implementation reproduced the remaining pool-starvation deadlock:

```text
Postgres_composed_creation_completes_and_concurrent_retry_plans_once
Failed: TaskCanceledException after 2 s
```

Migration 009 upgrades creation claims with an owner fencing token, finite lease expiry,
`InProgress`/`Completed` state, committed result plan ID, and update timestamp. Claim,
takeover, completion, and release each borrow a connection only for one short database
operation. Waiting callers release the pool and perform cancellation-aware polling; no
connection or transaction crosses planner/vendor I/O or a delay. Claimed manifest insertion
locks the claim row in the same database transaction and verifies the exact request, owner
token, active state, and unexpired lease before writing the plan, so a replaced owner cannot
persist stale work. A failed owner expires its lease immediately. An expired replacement owner
first checks the deterministic plan ID, so a crash after plan insert but before claim
completion repairs the claim without rerunning the planner.

Focused GREEN:

```text
DurablePlanServiceTests plus four named PostgreSQL creation-claim/composition tests
Passed: 18, Failed: 0, Skipped: 0, Total: 18, Duration: 1 s

Migrations_create_control_plane_and_are_idempotent
Passed: 1, Failed: 0, Skipped: 0, Total: 1, Duration: < 1 ms
```

The real PostgreSQL tests cover nine simultaneous identical calls on a two-connection pool,
one canceled waiter, one planner read, one committed plan/digest, explicit owner release,
expired-owner fencing takeover, rejection of stale-owner persistence, and committed-plan
recovery after abandonment before claim completion.

No formatter, linter, Pester, or project-wide test suite was run.

### Fix Round 4: Database-clock fencing, renewable ownership, and atomic results

Formal review rejected Round 3 because claim decisions accepted caller timestamps, a lease
could expire during planner/vendor I/O without renewal, and an existing deterministic PlanId
could be treated as successful recovery without proving that its manifest came from the exact
request/owner claim. Those were release blockers even though the earlier focused suite was
green.

Migration 010 adds the exact result digest to completed claims and requires completed claims
to bind both result PlanId and digest. Previously completed claims are reopened expired because
the older two-transaction path cannot prove that their manifest was created by the exact claim.
`InsertClaimedAsync` now locks and validates the live owner/request claim before plan insertion,
writes the manifest, and transitions the claim to `Completed` with the exact PlanId/digest in
the same PostgreSQL transaction. A lost response after commit is recoverable without planning;
an arbitrary directly inserted plan at the same deterministic identity is a conflict.

PostgreSQL uses `clock_timestamp()` while holding the claim row lock for acquire, renew,
takeover, release, and insert fencing; service-provided timestamps no longer participate in
ownership decisions. Creation owners run a brief-borrow renewal heartbeat while planner/vendor
I/O is in progress. Renewal ownership loss cancels and awaits planner work, and caller
cancellation expires the exact claim so a retry can acquire it. The production-composed test
uses a two-connection pool, a 150 ms initial lease, a planner read blocked for more than 400 ms,
eight concurrent retries, and one canceled waiter; it still performs one planner read and
commits one exact result.

Focused GREEN:

```text
DurablePlanServiceTests plus six named PostgreSQL claim/composition/migration tests
Passed: 23, Failed: 0, Skipped: 0, Total: 23, Duration: 1 s
```

## Decisions

- `EntitySyncPlanner.CreateSnapshotAsync` is the durable path into the existing matcher and mapper. It consumes caller-owned generation-pinned leases and does not add the transient planner result to the legacy process-local plan repository. Existing `CreateAsync` retains legacy behavior by adding only after the same snapshot path returns.
- `DurablePlanService.CreatePlanAsync` resolves the current exact enabled policy, resolves and acquires both exact enabled connection generations, invokes the planner once, rechecks active exclusions, seals the complete manifest, commits it, and re-reads the persisted plan before returning.
- Stable plan IDs derive through `EntitySyncCanonicalDigest` from tenant plus a required caller-supplied non-secret idempotency key. PostgreSQL permanently binds that identity to the canonical normalized request SHA-256 in a durable owner-token/fenced creation claim. Claim acquisition, polling, takeover, release, and renewal use only brief connection borrows; no connection is held during planner/vendor I/O or retry delay. The owner renews while planning and cancels/awaits stale work on ownership loss. Claimed manifest insertion and claim completion bind the exact request, owner, result PlanId, and result digest in one transaction. A retried compatible request returns only that atomically committed result without a second planner invocation; changed request bodies and arbitrary direct plans at the same deterministic identity are rejected. Item IDs and all content hashes use the shared canonical primitive. Source keys retain the normalized immutable source ID used by durable exclusions.
- Desired payloads contain only policy-allowed fields. Blocked property names are recursively removed from nested objects and arrays before payload/diff hashing. JSON objects are recursively key-sorted and explicit nulls are retained.
- `EntityFieldChange` stores ordered redacted before/desired JSON, independent SHA-256 values computed from the unredacted canonical values, and sensitivity. Sensitive plaintext is discarded before construction of every durable model. Persisted `field_diffs` retains the migration-005 three-key envelope while storing hash/sensitivity metadata inside the before/desired objects.
- Existing planner exclusions and exclusions introduced between planning and persistence both materialize as `None`/`PersistentExclusion` with a generic immutable reason. Credential-shaped match reasons are redacted.
- Page reads first retrieve the immutable page, then use PostgreSQL to get or open exactly one tenant/plan/digest/actor inspection session. Range IDs are deterministic for the actual inclusive stored ordinal range, duplicate inserts are idempotent, and completion occurs only after the immutable union is `[0,item_count)`. Completed sessions are read-only and reusable.
- Approval re-reads the plan, policy, connections, and actor-bound completed session. PostgreSQL uses the same policy-scoped advisory lock as policy writes and ordered shared connection-row locks, so policy/generation rotation and approval have one race-safe linearization point.
- `ApproveInspectionAsync` inserts the approval, plan transition, and digest-validated redacted audit event in one transaction. Audit event type, correlation, actor, plan, timestamp, and redacted payload hash are checked before mutation.
- Policy writes and plan creation/approval share a targeted tenant/policy advisory lock rather than a process-local lock or a table-wide policy lock.
- Route-scoped PostgreSQL advisory locks and database triggers serialize exclusion add/revoke, durable plan insertion, and Draft-to-Approved transitions. An exclusion that wins blocks actionable plan persistence or approval; an approved plan that wins blocks a conflicting exclusion.

## Changed Files

- `src/Application/DurablePlanService.cs`
- `src/Application/PlanManifestBuilder.cs`
- `src/Application/EntitySyncPlanner.cs`
- `src/Application/EntitySyncPlanDigest.cs`
- `src/Application/EntitySyncRequests.cs`
- `src/Core/EntitySyncPlanItem.cs`
- `src/Core/EntitySyncDurablePlan.cs`
- `src/Ports/IDurableSyncPlanRepository.cs`
- `src/Runtime/PostgresControlPersistence.cs`
- `src/Runtime/PostgresDurableSyncPlanRepository.cs`
- `src/Runtime/PostgresSyncPolicyRepository.cs`
- `db/migrations/005_control_operations.sql`
- `db/migrations/008_plan_exclusion_serialization.sql`
- `db/migrations/009_durable_plan_creation_claims.sql`
- `db/migrations/010_atomic_plan_creation_results.sql`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlPlaneMigrationTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/DurablePlanServiceTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlModelTests.cs`
- `Tests/LISSTech.EntitySync.Platform.Tests/ControlRepositoryTests.cs`
- `.superpowers/sdd/2026-08-31-entitysync-durable-control-plane/task-5-report.md`

## Self-Review

- Manifest construction preallocates the item array, does not retain full external entities, and uses bounded per-item dictionaries containing only allowed fields. Canonical `JsonDocument` instances are disposed immediately after cloning their immutable elements.
- Large PostgreSQL item insertion remains the Task 3 binary `COPY` path; deterministic retry checks digest and count without re-copying thousands of items.
- The manifest digest binds policy ID/version/hash, source and target IDs/generations, selection bounds, created/expiry timestamps, actor, ordered immutable item identities/actions/evidence, redacted payloads, payload hashes, and ordered field-change hashes/sensitivity.
- No second serializer/hash convention was introduced: plan identity, item IDs, item digests, field hashes, payload hashes, audit hashes, and the sealed manifest all use `EntitySyncCanonicalDigest`. Durable plan source keys deliberately use the same normalized immutable source-ID convention as permanent exclusions.
- Inspection and draft state exists only behind `IDurableSyncPlanRepository`; the service contains no mutable session dictionary, cache, or fallback repository.
- Connection and policy lock ordering is consistent across plan insert, approval, and policy writes. Approval conflicts leave no divergent approval/audit rows.
- Focused serialization assertions confirm top-level and recursively nested sensitive values plus blocked fields never occur in the persisted manifest projection.
- `git diff --check` passed.

## Concerns

None.
