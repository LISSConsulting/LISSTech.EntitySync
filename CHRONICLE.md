> Project working memory for Ralph agents.
> Keep this file compact: unresolved state only, not a changelog. Completed history belongs in git, releases, and .ralph/logs/*.jsonl.

## Current Focus

- Implementing specs/001-lcat-sync-adapter/tasks.md. T001 (Setup) done: created
  `src/Adapters/LCAT/` with `LCATOptions.cs` and a placeholder `LCATEntityAdapter.cs`
  implementing `IEntityAdapter`. Create/Update are final (`NotSupportedException`,
  batch-only per spec); `GetEntitiesAsync`/`TestConnectionAsync` are `NotImplementedException`
  stubs pending T019.
- T002 done: added "(planned)" LCAT stub notes to `docs/Connect-EntitySyncVendor.md`,
  `docs/New-EntitySyncPlan.md`, and `docs/Invoke-EntitySyncPlan.md`. Also created
  `docs/Get-EntitySyncEntity.md`, which never existed before despite the command
  existing in `src/Commands/GetEntitySyncEntityCommand.cs` (a pre-existing docs gap,
  not LCAT-specific). All LCAT syntax is documented as planned/future since the
  command surface (ValidateSet, dynamic parameters) does not accept `LCAT` until
  Phase 2 (T005-T008) and US1 (T021).
- T003 done: added a `#region LCAT test scaffolding` block in
  `Tests/LISSTech.EntitySync.Tests.ps1` (after `AfterAll`, before the first `It`)
  with `New-TestLCATOptions`/`New-TestLCATAdapter` helper functions, following the
  existing inline `[XOptions]::new()` + `[XEntityAdapter]::new($options)` pattern
  used for NCentral/Halo adapter tests. No HTTP-mocking helper exists anywhere in
  this test file for any vendor; adapter tests either validate before any network
  call or test private/static parsing methods via reflection (see the
  `HaloEntityAdapter.MapAddress` example) — future LCAT batch request/response
  tests (T015, T017, T039) should follow that reflection pattern rather than
  introducing new HTTP mocking infra. `just build` and `just test` both pass (51/51).
- T004 done: added "(planned)" LCAT references to `README.md` — a
  `### LCAT (planned)` subsection in Configuration (env vars, batch-only
  behavior, credential redaction), an "Adapters planned" note in Project
  Structure, and a "Planned:" line in Status pointing at
  `specs/001-lcat-sync-adapter/`. Matches the "(planned)"/future-tense style
  used in the T002 docs stubs. `tasks.md` checkboxes are intentionally left
  unmarked — prior T001-T003 commits never edited `tasks.md`; completion is
  tracked only via CHRONICLE.md and commit messages (specs are read-only per
  AGENTS workflow). `just build` and `just test` both pass (51/51).
  Phase 1 (Setup) is now complete. Next incomplete task: T005 (Phase 2 Foundational).
- T005 done: added `LCAT` and `LTAC` to the `Vendor` `ValidateSet` in
  `ConnectEntitySyncVendorCommand.cs`, plus a `NormalizeVendorAlias` helper
  (called from both `GetDynamicParameters` and `EndProcessing`) that maps
  `LTAC` -> `LCAT` per spec FR-002/contract (LTAC alias, plans always say
  LCAT). Added an explicit `LCAT` branch in `EndProcessing` that throws
  `NotImplementedException` — without it, `-Vendor LCAT` would silently fall
  through into the unconditional NCentral branch at the end of the method
  (that branch has no `if` guard of its own; it's the effective default).
  Real LCAT dynamic parameters (BaseUrl/BearerToken) and connection logic are
  T021 (US1), not this task. Verified manually: `-Vendor LCAT` and
  `-Vendor LTAC` both throw the same clear not-yet-implemented message, and
  tab completion after `-Vendor ` lists `LCAT`/`LTAC` alongside the existing
  vendors. `just build` and `just test` both pass (51/51).
- T006 done: added `LCAT`/`LTAC` to the `Vendor` `ValidateSet` in
  `GetEntitySyncEntityCommand.cs`, plus the same `NormalizeVendorAlias`
  helper pattern as T005 (called from `GetDynamicParameters` and
  `EndProcessing`) so `LTAC` reads still report as `LCAT`. Added a
  Customer-only `EntityType` completion branch (matching NetSuite's
  single-type pattern) and an explicit `LCAT` branch in `EndProcessing` that
  throws `NotImplementedException` before reaching `ConnectionRegistry.Get` —
  without it, `-Vendor LCAT` would fail with a confusing "not connected"
  error instead of a clear not-yet-implemented message, since
  `Connect-EntitySyncVendor -Vendor LCAT` still throws (T005) and can never
  register an adapter. Real LCAT reads are T019/T020 (US1), not this task.
  Verified manually: tab completion after `-Vendor LCAT -Type ` and
  `-Vendor LTAC -Type ` both offer only `Customer`, and invoking the command
  throws the clear not-yet-implemented message. `just build` and `just test`
  both pass (51/51).
- T007 done: added `LCAT`/`LTAC` to the `TargetVendor` `ValidateSet` in
  `NewEntitySyncPlanCommand.cs` (SourceVendor is unchanged — spec.md confirms
  LCAT is a sync target only, never a source), plus the same
  `NormalizeVendorAlias` helper pattern as T005/T006 (called from
  `GetDynamicParameters` and `EndProcessing`) so `LTAC` plans still identify
  the vendor as `LCAT`. `EntityTypesForVendor` already falls back to
  `["Customer"]` for any vendor that isn't HaloPSA/NCentral (this is how
  NetSuite gets Customer-only target completion today), so LCAT needed no
  new branch there. Added an explicit `LCAT` branch in `EndProcessing` that
  throws `NotImplementedException` before `ConnectionRegistry.Get(TargetVendor)`
  — without it, `-TargetVendor LCAT` would fail with a confusing "not
  connected" error instead of a clear not-yet-implemented message, since
  `Connect-EntitySyncVendor -Vendor LCAT` still throws (T005) and can never
  register an adapter. Real LCAT plan/target wiring is T022/T023 (US1), not
  this task. Verified manually: `-TargetVendor LCAT` and `-TargetVendor LTAC`
  both throw the same clear not-yet-implemented message. `just build` and
  `just test` both pass (51/51). Next incomplete task: T008
  (Test-EntitySyncConnection LCAT support).
- T008 done: added `LCAT`/`LTAC` to the `Vendor` `ValidateSet` in
  `TestEntitySyncConnectionCommand.cs`, plus the same `NormalizeVendorAlias`
  helper pattern as T005/T006/T007 (called from `EndProcessing`, since this
  command has no dynamic parameters) so `LTAC` reports as `LCAT`. Added an
  explicit `LCAT` branch in `EndProcessing` that throws
  `NotImplementedException` before `ConnectionRegistry.Get` — same rationale
  as T005/T006/T007: without it, `-Vendor LCAT` would fail with a confusing
  "not connected" error instead of a clear not-yet-implemented message. Real
  LCAT connection testing lands with T019 (US1). Verified manually:
  `-Vendor LCAT` and `-Vendor LTAC` both throw the same clear
  not-yet-implemented message. `just build` and `just test` both pass
  (51/51).
- T009 done: added an explicit `LCAT` branch to
  `EntitySyncLookupTypes.ForVendor` in `src/Core/EntitySyncLookupTypes.cs`
  returning `Array.Empty<string>()`. The unconditional fallback already
  returned empty for any unrecognized vendor (this is how NetSuite gets its
  empty lookup set today, with no explicit branch), so LCAT's `LookupTypes`
  was already empty before this change — verified manually via
  `[LCATEntityAdapter]::new(...).LookupTypes.Count` (0, before and after).
  Added the explicit branch anyway to document intent rather than rely on
  incidental fallthrough, matching the explicit-branch precedent set by
  T005-T008 for other command surfaces. No Pester test added: T009 has no
  associated test task in `tasks.md` (unlike the US1-3 phases), and
  T005-T008 established manual verification as sufficient for this kind of
  Foundational-phase task. `just build` and `just test` both pass (51/51).
- T010 done: added `lcat` to the `Tags` array in
  `Module/LISSTech.EntitySync.psd1` `PrivateData.PSData`. No
  `CmdletsToExport` change was needed — LCAT adds no new cmdlets of its own;
  it only extends existing commands (`Connect-EntitySyncVendor`,
  `Get-EntitySyncEntity`, `Test-EntitySyncConnection`, `New-EntitySyncPlan`,
  `Invoke-EntitySyncPlan`) that are already exported, confirmed by
  `just test-load` listing the same 12 commands as before with no gaps.
  Left `ReleaseNotes` and the pre-existing `ncentral` tag gap untouched —
  that drift predates this feature (the N-central adapter commit never
  touched Tags either) and is out of this task's scope. `just build`,
  `just test-load`, and `just test` all pass (51/51).
- T011 done: added `LCATCustomerScopeRequest` and `LCATSyncResult` model
  types plus private static `BuildSyncRequestBody`/`ParseSyncResponse`
  helpers to `src/Adapters/LCAT/LCATEntityAdapter.cs`, matching the exact
  field names in `contracts/lcat-sync-rpc.md` (`slug`, `display_name`,
  `ncentral_customer_id`, `ncentral_parent_customer_id`, `reason`, `ticket`
  on the request; `inserted_count`/`updated_count`/`retired_count`/
  `active_count`/`audit_event_id` on the response, read via the existing
  `JsonElement.GetInt`/`GetString` extensions in
  `src/Adapters/JsonObjectExtensions.cs`). Kept the two build/parse methods
  private static, following the `HaloEntityAdapter.MapAddress` reflection
  pattern called out in the T003 note, since no HTTP mocking infra exists
  in this test file and future US1/US2 tests (T015, T017, T033, T039) can
  invoke them the same way. Deliberately did not wire these into
  `GetEntitiesAsync`/`TestConnectionAsync`/a batch-send method or touch
  `LCATOptions`/HTTP headers — that connection/auth/send wiring is T018-T020
  (US1), and this task is Foundational-phase model support only. No slug
  format or duplicate-ID validation added here either; that is `DefaultEntityMapper.cs`'s
  job in T012/T043, not the adapter's request/response shape. Verified
  manually via reflection (`GetMethod(..., NonPublic Static)`) that
  `BuildSyncRequestBody` produces the exact contract JSON shape and
  `ParseSyncResponse` round-trips the sample contract response. No Pester
  test added, matching the T009 precedent that Foundational-phase tasks
  without a `tasks.md` test entry rely on manual verification. `just build`
  and `just test` both pass (51/51).
- T012 done: added a private static `IsValidLcatSlug` helper plus a
  `[GeneratedRegex]`-backed `LcatSlugPattern` partial method to
  `src/Mapping/DefaultEntityMapper.cs`, enforcing the exact slug contract
  from `contracts/lcat-sync-rpc.md`/`data-model.md`
  (`^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$`). Made `DefaultEntityMapper`
  `partial` (previously not) since `GeneratedRegex` requires a partial method
  on a partial type, following the existing `EntityNormalizer.cs` precedent
  for compiled regex helpers rather than a hand-rolled `Regex` field.
  Deliberately did not wire this into `MapCreate`/`MapUpdate`, add slug
  *generation* (deriving a slug from a display name), or add safe-failure
  reasons for unsafe/duplicate slugs — those are T022 (US1 customer
  mapping), T030 (US2 site-derived slug generation, also T028's test), and
  T043 (US3 safe-failure reasons), respectively; this task is Foundational
  validation-only. No Pester test added, matching the T009/T011 precedent
  that Foundational-phase tasks without a `tasks.md` test entry rely on
  manual verification. Verified manually via reflection
  (`GetMethod("IsValidLcatSlug", NonPublic Static)`) against contract
  examples, boundary lengths (64 chars valid, 65 invalid), leading/trailing
  dash rejection, embedded spaces, and underscore acceptance. `just build`
  and `just test` both pass (51/51). Phase 2 (Foundational) is now complete.
- T013 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1`
  (after the existing `Connect-EntitySyncVendor` parameter-completion test,
  before `Declares object output for Get-EntitySyncConnection`): one
  confirming `LCAT`/`LTAC` complete as vendors on `Get-EntitySyncEntity`,
  `Connect-EntitySyncVendor`, `Test-EntitySyncConnection`, and
  `New-EntitySyncPlan -TargetVendor` (and explicitly do NOT complete on
  `-SourceVendor`, matching spec.md's LCAT-is-target-only constraint), and
  one confirming `-Type`/`-TargetEntityType` completion for `LCAT`/`LTAC`
  offers only `Customer`. These exercise command-surface behavior already
  implemented in T005-T008 (ValidateSet + dynamic parameters), so both
  tests passed immediately with no product code changes — this is expected
  per tasks.md's "Tests for User Story 1" phase, which front-loads test
  authorship before the US1 implementation tasks (T018+) that add real
  LCAT connection/mapping/apply behavior. `just build` and `just test` both
  pass (53/53). Next incomplete task: T014 (Pester tests for NCentral
  Customer to LCAT slug/display/id mapping).
- T014 done: added four Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1`
  (after the T013 completion tests, before `Declares object output for
  Get-EntitySyncConnection`) calling `DefaultEntityMapper.MapCreate`/
  `MapUpdate` with source vendor `NCentral`/`Customer` and target vendor
  `LCAT`/`Customer`, asserting `request.Fields['display_name']`,
  `request.Fields['ncentral_customer_id']` (including fallback from
  `ExternalIds['NCentralCustomerId']` to `source.Id` per data-model.md),
  and `request.Fields['slug']` (validated against the contract regex
  `^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$` rather than pinning an exact
  slug algorithm, since slug derivation is T022/T030's job, and asserting
  determinism by mapping the same source twice and comparing slugs) using
  snake_case field keys (`display_name`, `ncentral_customer_id`, `slug`,
  `ncentral_parent_customer_id`) matching `contracts/lcat-sync-rpc.md`
  exactly, chosen so a future T022 can build `LCATCustomerScopeRequest`
  straight from `EntityWriteRequest.Fields` with no key translation. Also
  asserts `ncentral_parent_customer_id` is absent/null for customer-derived
  scopes (no parent) and that `MapUpdate` preserves `target.Id` as
  `request.Id`. Did not add tests for site-derived mapping (T026), missing
  parent safe-failure (T027), or registration-token exclusion (T036) — those
  are separate tasks with their own test entries. As expected for a
  test-first US1 task, all four tests currently fail since `MapCreate`/
  `MapUpdate` in `src/Mapping/DefaultEntityMapper.cs` has no LCAT branch yet
  (T022 implements it); this reproduces the T013 precedent in reverse — that
  task's tests passed immediately because the behavior pre-existed, this
  task's tests fail because the behavior doesn't yet. `just build` succeeds;
  `just test` reports 53 passed / 4 failed (expected failures, all in the
  new T014 tests).
- T015 done: added three Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1`
  (after the T014 mapping tests, before `Declares object output for
  Get-EntitySyncConnection`) invoking the private static
  `BuildSyncRequestBody`/`ParseSyncResponse` helpers on `LCATEntityAdapter`
  via reflection (`GetMethod(..., NonPublic Static)`), following the
  `HaloEntityAdapter.MapAddress` precedent from T003/T011. One test builds a
  two-item `List<LCATCustomerScopeRequest>` (a customer scope with no parent
  and a site scope with a parent) — passed to `Invoke` as `@(, $customers)`
  so PowerShell doesn't enumerate the list into separate arguments — and
  asserts the serialized JSON matches `contracts/lcat-sync-rpc.md` exactly:
  `customers[].slug/display_name/ncentral_customer_id/ncentral_parent_customer_id`,
  top-level `reason` (`EntitySync N-central to LCAT sync`), and a present-but-null
  `ticket` key. Two more tests cover `ParseSyncResponse`: one with a full
  contract-shaped response asserting all five fields
  (`InsertedCount`/`UpdatedCount`/`RetiredCount`/`ActiveCount`/`AuditEventId`),
  one with `{}` asserting the existing `GetInt`/`GetString` extension
  fallbacks already produce 0/null defaults rather than throwing. These tests
  exercise T011's existing implementation with no product code changes
  needed (T011 already matched the contract shape), unlike T014 which is
  still red pending T022. `just build` succeeds; `just test` reports 56
  passed / 4 failed (the same pre-existing T014 failures, now joined by 3
  new passing T015 tests).
- T016 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1`
  (after the T015 adapter tests, before `Declares object output for
  Get-EntitySyncConnection`) that invoke the real `New-EntitySyncPlan` cmdlet
  end-to-end for an NCentral Customer -> LCAT Customer plan, registering
  `NCentralEntityAdapter`/`LCATEntityAdapter` test instances directly into
  `LISSTech.EntitySync.Runtime.ConnectionRegistry` (bypassing
  `Connect-EntitySyncVendor`, which still throws `NotImplementedException`
  for LCAT per T005) and piping `ExternalEntity` sources so no NCentral read
  is needed either. One test asserts a two-source plan comes back with
  `TargetVendor 'LCAT'`, zero `TargetCandidates`, and both items `Action
  'Create'`/`MatchType 'NoMatch'` with a "No target candidate found" reason;
  the other confirms `-TargetVendor LTAC` still produces `TargetVendor
  'LCAT'` in the resulting plan. Chose zero `TargetCandidates` deliberately:
  `LCATEntityAdapter.GetEntitiesAsync` is still a T019 stub, and this repo's
  established convention (T003 note) is no HTTP-mocking infra, so a
  LCAT-target plan must not depend on a live read to stay network-free in
  Pester — this locks in that constraint for whoever implements T023
  ("Preserve LCAT target candidates ... during NCentral Customer planning"),
  same as `LCATEntityAdapter.CreateEntityAsync`/`UpdateEntityAsync` always
  throwing `NotSupportedException` proves no per-item vendor write can occur
  during planning by construction. While wiring these tests up, found and
  fixed a real bug in the T003 scaffolding: `New-TestLCATOptions`/
  `New-TestLCATAdapter` were defined directly in the `Describe` body, which
  in Pester v5 only runs during the discovery pass, so both functions were
  `CommandNotFoundException` inside any `It` block (never previously
  exercised, since T013-T015 built LCAT options/entities inline instead of
  calling these helpers) — moved both function definitions into the existing
  top-level `BeforeAll` so they're in scope during the run phase. Both new
  tests fail today with `NotImplementedException: LCAT sync plan targets are
  implemented in a later EntitySync task.` (the T007 guard in
  `NewEntitySyncPlanCommand.EndProcessing`), the same expected-red state as
  T014's still-pending mapping tests. `just build`, `just test-load`, and
  `just test` all succeed/pass; `just test` reports 56 passed / 6 failed (4
  pre-existing T014 failures plus the 2 new expected T016 failures).
- T017 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1`
  (after the T016 plan-creation tests, before `Declares object output for
  Get-EntitySyncConnection`) that build an `EntitySyncPlan`/`EntitySyncPlanItem`
  graph directly (bypassing `New-EntitySyncPlan`, which still throws for
  `-TargetVendor LCAT` per T007/T016) and call the real `Invoke-EntitySyncPlan`
  cmdlet with `-Apply -WhatIf -PassThru` against a registered test
  `LCATEntityAdapter`. Since neither `*>&1` redirection nor swapping
  `[Console]::Out` captures a compiled cmdlet's `ShouldProcess`/`-WhatIf`
  confirmation text in-process (verified manually both ways: `*>&1` yields
  zero captured objects, and swapping `[Console]::Out` silently breaks all
  further host output for the rest of the process), both tests wrap the call
  in `Start-Transcript`/`Stop-Transcript` to a per-test GUID-named temp file
  and count lines matching `What if:` — this reliably captured the
  confirmation text with no flakiness across a full `just test` run. One test
  asserts a two-item approved Create plan produces exactly one `What if:`
  confirmation mentioning `LCAT` (proving the whole batch is confirmed once,
  per `contracts/lcat-sync-rpc.md`'s "must not use one request per normal plan
  item" and plan.md's "LCAT-specific batch branch... over per-item adapter
  writes"), not one per item; the other adds a third `Review`-action item and
  asserts the confirmation count is still 1 (the Review item must not join
  the batch or need its own confirmation) while the `-PassThru` output still
  reports exactly one `Review` result for it (pre-existing generic behavior,
  a sanity check). Deliberately did not attempt to assert on real batch send
  success/counts (T020) or non-success-response redaction (T039) — both
  require an actual HTTP round trip, and this repo's established convention
  (T003 note) is no HTTP-mocking infra; `-WhatIf` keeps both new tests
  network-free by construction, matching how the rest of the suite validates
  before any network call. Also deliberately did not test Status-based
  approval filtering (Accepted/Rejected/NoUpdate from the review workbook) —
  `InvokeEntitySyncPlanCommand.ProcessRecord` only branches on `item.Action`
  today, not `item.Status`; filtering by Status is T042 (US3), not this task.
  Both new tests fail today with `Expected 1, but got 2` (current per-item
  `ShouldProcess` calls one confirmation per Create item, since no
  LCAT-specific batch branch exists in `InvokeEntitySyncPlanCommand` yet —
  that's T024). `just build` succeeds; `just test` reports 56 passed / 8
  failed (the same 6 pre-existing T014/T016 failures, now joined by 2 new
  expected T017 failures). Next incomplete task: T018 (LCAT connection
  options with base URL and bearer credential).

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|

## Decisions To Preserve

-
