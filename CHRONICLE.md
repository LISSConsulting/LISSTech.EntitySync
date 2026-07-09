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
  expected T017 failures).
- T018 assessed as already satisfied: `LCATOptions.cs` has carried plain `BaseUrl`/`BearerToken`
  string properties since T001, matching the exact shape of every other vendor's Options POCO
  (`NCentralOptions.BaseUrl`/`UserApiToken`, `HaloOptions.BaseUrl`/`AccessToken`) — none of which
  hold validation logic. Confirmed via `ConnectEntitySyncVendorCommand.cs` that URL/credential
  validation (`Require`, `ValidateAbsoluteHttpsUrl`) universally lives in the Connect command
  (T021), never in the Options class itself. `git log` confirms `LCATOptions.cs` has had no
  commits since T001. No code change made for T018; moved directly to T019.
- T019 done: implemented `LCATEntityAdapter`'s connection validation, Customer reads, and
  confirmed no per-item create/update path in `src/Adapters/LCAT/LCATEntityAdapter.cs`. Wired the
  constructor to set `httpClient.BaseAddress` (from `options.BaseUrl`, trailing-slash normalized
  like `NCentralEntityAdapter`), an `Accept: application/json` header, and
  `Authorization: Bearer <options.BearerToken>` (matching `contracts/lcat-sync-rpc.md`'s
  "LCAT operator credential in the authorization header"), so both `TestConnectionAsync` and the
  future T020 batch-send reuse the same authenticated client. `GetEntitiesAsync` now throws
  `NotSupportedException` for any `EntityType` other than `Customer` (matching the
  NetSuite/NCentral adapter precedent) and otherwise returns an empty list — no LCAT
  customer-scope list/read endpoint exists in the sync RPC contract, and
  `contracts/powershell-command-contract.md` explicitly allows "reads may return an empty set so
  N-central sources plan as create/sync candidates" when no read surface exists. This empty-list
  behavior is what the still-pending T016 plan-creation tests (`TargetCandidates.Count` asserted
  as `0`) already lock in for whenever T023 wires real LCAT target reads into
  `NewEntitySyncPlanCommand`. `TestConnectionAsync` sends a real `GET` to the LCAT base URL root
  (PostgREST convention: root returns the service's OpenAPI schema) and returns
  `response.IsSuccessStatusCode`, mirroring the lightweight-endpoint pattern used by
  `NCentralEntityAdapter.TestConnectionAsync` (`GET api/auth/validate`) and
  `HaloEntityAdapter.TestConnectionAsync` (`GET api/client?count=1`). `CreateEntityAsync`/
  `UpdateEntityAsync` were already `NotSupportedException` from T001/T011 and needed no change —
  verified they still throw before any HTTP call, so per-item LCAT writes remain impossible by
  construction. Did not touch `TestEntitySyncConnectionCommand.cs` (still throws
  `NotImplementedException` for LCAT per T008, since wiring `Test-EntitySyncConnection -Vendor
  LCAT` through to this adapter is part of T021/US1 connection registration, not this task) or add
  a Pester test (T019 has no dedicated test entry in `tasks.md`, matching the T009/T011/T012
  Foundational-task precedent of manual-only verification for adapter-shell tasks without an
  explicit test task). Verified manually via a standalone PowerShell session: `GetEntitiesAsync`
  with `EntityType 'Customer'` returns an empty array with no network call, and `EntityType 'Site'`
  throws `NotSupportedException` with a clear message. `just build`, `just test-load`, and `just
  test` all succeed; `just test` reports the same 56 passed / 8 failed as before (the pre-existing
  T014/T016/T017 failures, unchanged — confirms no regression and no accidental fix of
  still-pending US1 work). Next incomplete task: T020 (LCAT batch sync method for approved
  customer-scope requests).
- T020 done: added a public `SyncCustomerScopesAsync(IReadOnlyList<LCATCustomerScopeRequest>,
  CancellationToken)` method to `src/Adapters/LCAT/LCATEntityAdapter.cs` that serializes the
  batch via T011's existing `BuildSyncRequestBody`, POSTs it to the contract's
  `rpc/sync_ncentral_customers` path (new `SyncPath` const, relative to the trailing-slash-normalized
  `httpClient.BaseAddress` set in the constructor, PostgREST RPC convention per
  `contracts/lcat-sync-rpc.md`), and returns `ParseSyncResponse`'s parsed `LCATSyncResult` on
  success. On a non-success status, throws `InvalidOperationException` including only the HTTP
  status/reason and the endpoint path — deliberately no response-body preview, unlike
  `NCentralEntityAdapter`'s error-message convention, since `contracts/lcat-sync-rpc.md`'s
  Response Rules only guarantee status+path in operator-facing errors and full redaction
  hardening (stripping the `Authorization` header/any credential echoes from adapter exceptions)
  is explicitly T040 (US3), not this task. Made the method public (not private, unlike
  `BuildSyncRequestBody`/`ParseSyncResponse`) since T024 (`InvokeEntitySyncPlanCommand`'s LCAT
  batch apply branch) will need to call it directly on a resolved `LCATEntityAdapter` instance,
  the same way `InvokeEntitySyncPlanCommand.cs` already casts `ConnectionRegistry.Get("HaloPSA")`
  to `HaloEntityAdapter` for `UpsertNCentralClientLinkAsync`/`UpsertNCentralSiteLinkAsync`. Did
  not wire this into `InvokeEntitySyncPlanCommand.cs` or add plan-item-to-`LCATCustomerScopeRequest`
  translation — that command/mapping wiring is T022-T024, not this task. T020 has no dedicated
  test entry in `tasks.md` (T015/T017 already cover `BuildSyncRequestBody`/`ParseSyncResponse`
  and the `-WhatIf` batch-confirmation-count behavior respectively), matching the T009/T011/T012/T019
  Foundational/adapter-shell precedent of manual-only verification; confirmed manually via
  reflection that `SyncCustomerScopesAsync` exists with the expected `Task<LCATSyncResult>`
  return type and `(IReadOnlyList<LCATCustomerScopeRequest>, CancellationToken)` signature.
  `just build` and `just test` both succeed; `just test` reports the same 56 passed / 8 failed as
  before (unchanged pre-existing T014/T016/T017 failures — no regression, no accidental fix of
  still-pending US1 work). Next incomplete task: T021 (register `Connect-EntitySyncVendor -Vendor
  LCAT` dynamic parameters and environment fallbacks).
- T021 done: replaced the `NotImplementedException` LCAT branch in
  `ConnectEntitySyncVendorCommand.cs`'s `EndProcessing` with real registration, following the exact
  `Require(...)`/env-fallback pattern already used for `NCentralBaseUrl`/`NCentralUserApiToken` and
  the same `ValidateAbsoluteHttpsUrl` helper NCentral uses for its base URL. Added an `else if
  (Vendor.Equals("LCAT", ...))` branch to `GetDynamicParameters` registering `LCATBaseUrl`/
  `LCATBearerToken` (both plain `string` dynamic parameters, no defaults, matching
  `contracts/powershell-command-contract.md`'s exact parameter names), and in `EndProcessing`
  builds an `LCATOptions` from `Require(..., "LCAT_BASE_URL", "LCATBaseUrl")` /
  `Require(..., "LCAT_BEARER_TOKEN", "LCATBearerToken")` (env var names match
  `docs/Connect-EntitySyncVendor.md`/`README.md`'s existing "(planned)" documentation exactly, so no
  doc changes were needed), constructs `LCATEntityAdapter`, registers it via
  `ConnectionRegistry.Set`, and returns an `EntitySyncConnection` — the same shape as every other
  vendor branch. Did not add any LCAT-specific connection probe (e.g. calling
  `TestConnectionAsync` inline): NCentral/NetSuite connect the same way, constructing the adapter
  and registering it without an eager network round-trip; only HaloPSA fetches a token inline
  because OAuth token acquisition is unavoidable at connect time for that vendor. Verified manually
  that `EntitySyncConnection` (`Vendor`/`Adapter` only) still never exposes `LCATBearerToken` -
  confirmed via `Format-List *` on the returned object - and that `-Vendor LTAC` still normalizes
  to `Vendor 'LCAT'` in the result, matching FR-002. `just build` succeeds; `just test` reports the
  same 56 passed / 8 failed as before T021 (unchanged pre-existing T014/T016/T017 failures - no
  test in the suite pinned the old `NotImplementedException` behavior for `Connect-EntitySyncVendor`
  itself, so nothing needed updating). Next incomplete task: T022 (map NCentral Customer sources to
  LCAT Customer fields in `DefaultEntityMapper.cs`), which is what unblocks the currently-red T014
  mapping tests.
- T022 done: added a private `AddLcatCustomerScopeFields` step to `MapCreate`/`MapUpdate` in
  `src/Mapping/DefaultEntityMapper.cs`, gated on `targetVendor == LCAT` and `source.Vendor ==
  NCentral && source.EntityType == Customer` (Site-derived mapping is explicitly out of scope —
  that's T030/US2), setting `request.Fields["display_name"]` from `source.Name`,
  `request.Fields["ncentral_customer_id"]` from `source.GetExternalId("NCentralCustomerId")` falling
  back to `source.Id` (data-model.md's documented fallback), and `request.Fields["slug"]` via a new
  `DeriveLcatSlug` helper. Deliberately left `ncentral_parent_customer_id` unset for customer-derived
  scopes rather than writing an explicit null — data-model.md documents it as "empty for
  customer-derived scopes" and the T014 test only conditionally asserts null/empty *if* the key is
  present; T030 (US2 site mapping) is what will actually populate this key. `DeriveLcatSlug` reuses
  the existing `LcatSlugPattern` (`IsValidLcatSlug`, added in T012) rather than inventing a second
  validation regex: it replaces runs of characters outside `[A-Za-z0-9_-]` with a single `-` via a
  new `[GeneratedRegex]`-backed `LcatSlugSeparatorPattern`, trims leading/trailing `-`, truncates to
  64 chars (re-trimming trailing `-` after truncation), and falls back to `customer-{id}` only if the
  result still fails `IsValidLcatSlug` (e.g. an empty/all-punctuation name) — this is deterministic
  (same input always produces the same slug, as T014's two-call comparison test requires) without
  attempting cleanco-style legal-suffix stripping like `EntityNormalizer.NormalizeName` does for
  matching (that normalizer lowercases and drops suffix words for fuzzy-match comparison, which is a
  different job than producing a *display-preserving* LCAT slug). Did not touch
  `NewEntitySyncPlanCommand.cs` (T023) or `InvokeEntitySyncPlanCommand.cs` (T024) — this task is
  mapping-only. `just build` succeeds; `just test` now reports 60 passed / 4 failed, i.e. all four
  previously-red T014 tests now pass with no regressions, leaving only the pre-existing T016 (2) and
  T017 (2) failures that are blocked on T023/T024, not this task. Next incomplete task: T023
  (preserve LCAT target candidates and Customer-only target defaults during NCentral Customer
  planning in `NewEntitySyncPlanCommand.cs`), which is what unblocks the currently-red T016 tests.
- T023 done: removed the `NotImplementedException` guard for `TargetVendor LCAT` from
  `NewEntitySyncPlanCommand.EndProcessing` in `src/Commands/NewEntitySyncPlanCommand.cs` (added in
  T007) and let the existing generic plan pipeline run unchanged. No other change was needed: T019's
  `LCATEntityAdapter.GetEntitiesAsync` already returns an empty Customer set (preserving
  `TargetCandidates.Count == 0` for LCAT targets by construction, since no LCAT read endpoint
  exists), and `EntityTypesForVendor`'s existing catch-all fallback (`return new[] { "Customer" }`,
  predating this feature) already gives LCAT its Customer-only default/completion with no new
  branch required. Confirmed no other command-surface change was needed by tracing the full
  `EndProcessing` path for a pipeline-sourced NCentral Customer -> LCAT plan: `ConnectionRegistry.Get`
  now succeeds for both vendors, `usingHaloNCentralLinks`/`usingHaloNCentralSiteLinks` stay false
  (source adapter isn't `HaloEntityAdapter`), and unmatched sources fall through to the generic
  `NoMatch`/`Create`-if-`-CreateMissing` branch already present in `CreatePlanItem`. `just build`
  succeeds; `just test` now reports 62 passed / 2 failed, i.e. both previously-red T016 tests
  (`Creates an NCentral Customer to LCAT plan...`, `Normalizes the LTAC target alias...`) now pass
  with no regressions, leaving only the pre-existing T017 batch-confirmation-count failures that are
  blocked on T024, not this task. Next incomplete task: T024 (LCAT customer batch apply branch with
  `-Apply`/`-WhatIf`/`ShouldProcess`/`-PassThru` support in `InvokeEntitySyncPlanCommand.cs`), which
  is what unblocks the currently-red T017 tests.
- T024 done: added an `ApplyLcatBatch` branch to `InvokeEntitySyncPlanCommand.ProcessRecord` in
  `src/Commands/InvokeEntitySyncPlanCommand.cs`, taken whenever `Plan.TargetVendor` is `LCAT`, that
  replaces the generic per-item write loop entirely for that plan (matching plan.md's "LCAT-specific
  batch branch... over per-item adapter writes" structure decision). The branch still honors the
  existing None/Review/`!Apply` per-item short-circuits (Review is written as its own
  `Success = false` result and never joins the batch; `!Apply` still writes "Planned only" per item),
  then maps every remaining Create/Update/Link item via `mapper.MapCreate`/`MapUpdate` (branching on
  `item.Target != null`, same convention as the generic loop), converts each `EntityWriteRequest`'s
  snake_case `Fields` (`slug`/`display_name`/`ncentral_customer_id`/`ncentral_parent_customer_id`,
  set by T022) into an `LCATCustomerScopeRequest` via a new `ToLcatCustomerScopeRequest` helper, and
  issues exactly one `ShouldProcess("{count} customer scope(s)", "Sync approved customer scopes to
  LCAT")` call for the whole batch before calling `LCATEntityAdapter.SyncCustomerScopesAsync` (T020)
  once — this is what the T017 tests lock in (one `What if:` line mentioning `LCAT` regardless of
  item count, and a Review item never counted toward or blocking that single confirmation). On
  `-WhatIf`, `ShouldProcess` returns false and the method returns before ever touching the adapter,
  so no `SyncCustomerScopesAsync` call happens in dry-run mode, matching every other vendor's
  `-WhatIf` behavior in this command. On a real apply, one `EntityWriteResult` per batched item is
  written (all sharing the same aggregate inserted/updated/retired/active counts from the single
  LCAT response, with `Raw` set to the full `LCATSyncResult`) since LCAT's sync endpoint returns
  batch-wide counts, not a per-item outcome. Deliberately did not add Status-based filtering
  (Reject/NoUpdate/unsafe/duplicate item exclusion) — confirmed via `EntitySyncPlanWorkbook.cs:384-388`
  that Reject/No Update decisions already convert to `Action = "None"` before reaching this command,
  so the existing None-skip covers them for now; explicit safe-failure-reason filtering is T042/T043
  (US3), not this task. `just build` succeeds; `just test` now reports all 64 tests passing (0
  failed), i.e. both previously-red T017 tests now pass with no regressions. Next incomplete task:
  T025 (update public command help for the customer sync flow in `docs/Connect-EntitySyncVendor.md`,
  `docs/New-EntitySyncPlan.md`, and `docs/Invoke-EntitySyncPlan.md`), the last task in Phase 3 (US1).
- T025 done: replaced the three "(planned)"/future-tense LCAT sections written in T002 with
  present-tense docs matching what T018-T024 actually shipped. `docs/Connect-EntitySyncVendor.md`
  gains a real `Connect-EntitySyncVendor -Vendor LCAT [-LCATBaseUrl <String>] [-LCATBearerToken <String>]`
  SYNTAX line and an Example 2 showing the env-var fallback pattern; the DESCRIPTION paragraph drops
  "will" wording and notes the `LTAC` alias normalizes on the returned connection.
  `docs/New-EntitySyncPlan.md`'s Example 4 now shows only the NCentral Customer -> LCAT Customer
  case with `-CreateMissing` (dropped the Site line from the old planned example — site-derived
  mapping is still T030/US2, not yet implemented, so documenting it now would be inaccurate) and
  explains LCAT never returns target candidates (no customer-scope read endpoint), so every source
  plans as `Create`/`NoMatch`. `docs/Invoke-EntitySyncPlan.md`'s "NCentral to LCAT (planned)" section
  is now "## NCentral to LCAT" and describes the actual `ApplyLcatBatch` behavior verified by reading
  `InvokeEntitySyncPlanCommand.cs`: single `ShouldProcess` confirmation for the whole batch, `None`
  skipped, `Review` written as its own unsuccessful result outside the batch, `-PassThru` returning
  one `EntityWriteResult` per batched item all sharing the same aggregate inserted/updated/retired/
  active counts (`Raw` = full `LCATSyncResult`), plus a new Example 4. Did not touch
  `docs/Get-EntitySyncEntity.md` (that LCAT read-behavior doc update is T046, Phase 6 Polish, not
  this task) or `README.md` (T034/T047 own the README LCAT sections). `just build` and `just test`
  both pass (64/64, no regressions — docs-only change). Phase 3 (User Story 1 / MVP) is now complete.
  Next incomplete task: T026 (Phase 4 / US2 — Pester tests for NCentral Site to LCAT payload mapping
  with `ncentral_parent_customer_id`).
- T026 done: added three Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T017 batch
  tests, before `Declares object output for Get-EntitySyncConnection`) calling `DefaultEntityMapper.MapCreate`/
  `MapUpdate` with source vendor `NCentral`/`Site` and target vendor `LCAT`/`Customer`, asserting
  `request.Fields['display_name']`, `request.Fields['ncentral_customer_id']` (the site's own effective
  id — `NCentralSiteId` falling back to `source.Id`, matching data-model.md's "N-central identifier for
  the source customer or source site"), `request.Fields['ncentral_parent_customer_id']` (the parent
  customer's id from `source.ExternalIds['NCentralCustomerId']`, per data-model.md's "Parent N-central
  customer identifier for site-derived scopes"), and `request.Fields['slug']` (validated against the
  contract regex only, not a pinned algorithm — deterministic site-derived slug generation using parent
  + site names is T028, not this task). One test covers create with both external ids present, one
  covers the site-id fallback to `source.Id` when `NCentralSiteId` is absent, one covers `MapUpdate`
  preserving `target.Id`. Did not add tests for missing-parent safe failure (T027) or site-derived slug
  determinism (T028) — those are separate tasks with their own test entries. As expected for a
  test-first US2 task, all three tests currently fail since `AddLcatCustomerScopeFields` in
  `src/Mapping/DefaultEntityMapper.cs` only branches on `source.EntityType == Customer` (added in T022)
  and has no Site branch yet (T030 implements it) — mirrors the T014 red-test precedent. `just build`
  succeeds; `just test` reports 64 passed / 3 failed (the 3 new expected T026 failures, no regressions
  in the previously-passing 64). Next incomplete task: T027 (Pester tests for missing site parent
  N-central customer ID safe failure).
- T027 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T026 mapping
  tests, before `Declares object output for Get-EntitySyncConnection`) that call the real
  `New-EntitySyncPlan` cmdlet end-to-end (same registered-adapter/pipeline pattern as T016) with an
  NCentral Site source that has `NCentralSiteId` but no `NCentralCustomerId` external id (i.e. no
  parent), targeting `LCAT`/`Customer`. Both assert the resulting plan item is blocked with `Action
  'Review'` and a `Reasons` entry matching `parent N-central customer identifier` rather than being
  silently created — one with `-CreateMissing` set (proving the missing-parent safe failure overrides
  `-CreateMissing`, since spec.md's edge case says the item must be blocked regardless) and one without
  it. Chose `Action 'Review'` (not a terminating error) to match spec.md's "blocked for review or fails
  safely with a clear non-secret reason" wording and the existing `IntegrationLinkConflict`/
  `IntegrationLinkTargetMissing` precedent in `NewEntitySyncPlanCommand.CreatePlanItem`, which already
  returns `Action 'Review'` with a descriptive `Reasons` entry for other plan-time safety problems
  rather than throwing. Did not pin an exact `MatchType` value (e.g. a new `MissingParent` type) since
  `tasks.md` leaves that to T031's implementation and only the `Reasons` text is part of this task's
  contract; whoever implements T031 should add the missing-parent check in `CreatePlanItem` (or a
  helper it calls) gated on `TargetVendor == LCAT` and `source.EntityType == Site` with no
  `NCentralCustomerId`/`source.Id` fallback present, consistent with data-model.md's site validation
  rule. As expected for a test-first US2 task, both tests currently fail — one on `Action` (`Review`
  expected, `Create` returned because `-CreateMissing` currently drives NoMatch sources straight to
  `Create` with no LCAT-specific parent check), the other on the `Reasons` regex (currently just "No
  target candidate found") — since no plan-time LCAT parent validation exists yet (T031 implements it).
  This is on top of the still-red T026 failures (blocked on T030, a separate task). `just build`
  succeeds; `just test` reports 64 passed / 5 failed (the 3 pre-existing T026 failures plus the 2 new
  expected T027 failures, no regressions in the previously-passing 64). Next incomplete task: T028
  (Pester tests for site-derived slug generation using parent customer and site names).
- T028 done: added three Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T027
  missing-parent tests, before `Declares object output for Get-EntitySyncConnection`) calling
  `DefaultEntityMapper.MapCreate` with NCentral Site sources whose `Name` is the site's own name only
  (e.g. `Main Office`, matching how `NCentralEntityAdapter.MapSite` actually populates `Name` — it
  never prefixes with the parent, confirmed by reading `MapSite`/`AddParentCustomerNamesAsync` in
  `src/Adapters/NCentral/NCentralEntityAdapter.cs`, unlike the T026 tests' illustrative compound
  `"Parent - Site"` names) with the parent name supplied via the same `NCentralCustomerName` custom
  field the real adapter sets on sites. One test asserts two sites sharing the identical site name
  (`Main Office`) under two different parent customers produce two *different* slugs (proving parent
  context factors into slug derivation, since the contract's own example disambiguates
  `Arista-Air-Conditioning-Main-Office` from a same-named site under a different parent, and
  spec.md's edge case "Two source records would produce the same LCAT slug" requires this); one
  asserts calling `MapCreate` twice on the same site source yields the identical slug (determinism);
  one asserts changing only the parent customer name (site id/name held constant) changes the derived
  slug, isolating that the parent name specifically is an input rather than incidentally passing
  because the parent id differed. Deliberately did not pin an exact slug string or algorithm (e.g. the
  contract's literal `Arista-Air-Conditioning-Main-Office` example) — same rationale as T014/T026:
  `DeriveLcatSlug`'s exact concatenation/suffix-handling is T030's implementation choice, and T022's
  chronicle note already established this mapper deliberately skips cleanco-style legal-suffix
  stripping, so pinning the contract's literal example string would fail even a reasonable
  implementation. As expected for a test-first US2 task, all three tests currently fail: `slug` (and
  every other LCAT field) comes back `$null` because `AddLcatCustomerScopeFields` in
  `src/Mapping/DefaultEntityMapper.cs` still only branches on `source.EntityType == Customer` (T030
  adds the Site branch and site-aware slug derivation). This is on top of the still-red T026/T027
  failures (blocked on T030/T031, separate tasks). `just build` succeeds; `just test` reports 64
  passed / 8 failed (the 5 pre-existing T026/T027 failures plus 3 new expected T028 failures, no
  regressions in the previously-passing 64). Next incomplete task: T029 (Pester tests for NCentral
  Site to LCAT plan creation and batch payload composition).
- T029 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T028 slug
  tests, before `Declares object output for Get-EntitySyncConnection`). One calls the real
  `New-EntitySyncPlan` cmdlet end-to-end (same registered-adapter/pipeline pattern as T016/T027) with
  an NCentral Site source that *has* a valid `NCentralCustomerId` parent, asserting `Action 'Create'`/
  `MatchType 'NoMatch'`/zero `TargetCandidates` — this mirrors T016's Customer-plan assertions for a
  Site source and passes immediately with no product code changes, since `CreatePlanItem` has no
  LCAT-specific branch of any kind yet (T031 adds the missing-parent check; a present parent takes the
  same generic NoMatch/Create path Customers already take), matching the T013 precedent where a test
  exercising pre-existing generic behavior is expected to go green on the first run. The second test
  maps one NCentral Customer and one NCentral Site source via `DefaultEntityMapper.MapCreate`, converts
  each resulting `EntityWriteRequest` into an `LCATCustomerScopeRequest` via reflection on
  `InvokeEntitySyncPlanCommand.ToLcatCustomerScopeRequest` (private static, added in T024 — reflected
  the same way T003/T015 reflect other private static helpers), then serializes both through
  `LCATEntityAdapter.BuildSyncRequestBody` (T011/T015) and asserts the resulting JSON's second
  `customers[]` entry carries the site's own id as `ncentral_customer_id` and the parent customer's id
  as `ncentral_parent_customer_id`. This spans mapper -> command conversion -> adapter serialization in
  one assertion specifically to pin the full site-derived payload shape without any HTTP mocking (this
  repo's established no-mocking-infra convention per the T003 note), and is the part of T029 that is
  genuinely red today: `AddLcatCustomerScopeFields` in `src/Mapping/DefaultEntityMapper.cs` still
  returns immediately for `source.EntityType == Site` (only handles Customer, added in T022), so the
  site's `EntityWriteRequest.Fields` come back empty and `ToLcatCustomerScopeRequest` defaults
  `NCentralCustomerId` to `string.Empty` — the test asserts `'702'`, so it fails until T030 populates
  Site fields. Did not test the `-WhatIf` single-batch-confirmation behavior for a mixed Customer+Site
  plan (already covered generically by T017 and not entity-type-gated in `ApplyLcatBatch`, so it would
  add no new red coverage) or missing-parent safe failure (that's T027, already done). `just build`
  succeeds; `just test` reports 65 passed / 9 failed (the 8 pre-existing T026/T027/T028 failures plus
  the 1 new expected T029 failure, no regressions). Next incomplete task: T030 (extend LCAT mapping for
  NCentral Site sources with site ID, display name, slug, and parent customer ID in
  `src/Mapping/DefaultEntityMapper.cs`), the first implementation task in Phase 4 (US2).
- T030 done: added a Site branch to `AddLcatCustomerScopeFields` in
  `src/Mapping/DefaultEntityMapper.cs`, checked before the existing Customer branch (which now
  returns early for any `EntityType` other than `Customer`). For Site sources it sets
  `request.Fields["display_name"]` from `source.Name`, `request.Fields["ncentral_customer_id"]`
  from `source.GetExternalId("NCentralSiteId")` falling back to `source.Id` (the site's own
  effective id, matching data-model.md), and `request.Fields["ncentral_parent_customer_id"]` from
  `source.GetExternalId("NCentralCustomerId")` with no fallback (left unset when absent — turning a
  missing parent into a plan-time Review block is T031's job, not this task's). Slug derivation
  reuses the existing `DeriveLcatSlug` helper (T012/T022, unchanged) but feeds it a
  parent-qualified basis string (`"{parentName} {siteName}"`, where parentName is
  `source.GetCustomField("NCentralCustomerName")` falling back to the parent id, or the site name
  alone if neither is present) — this is what makes two same-named sites under different parents
  produce different slugs (T028's contract scenario) while staying deterministic (same source
  always reproduces the same slug). Did not add plan-time missing-parent validation (T031) or touch
  `InvokeEntitySyncPlanCommand.cs`'s batch apply path (T032) — this task is mapping-only. `just
  build` succeeds; `just test` now reports 72 passed / 2 failed: all three previously-red T026 tests
  and all three previously-red T028 tests now pass with no regressions, leaving only the two T027
  missing-parent tests still red (blocked on T031, not this task). Next incomplete task: T031 (add
  plan-time validation reasons for missing NCentral site parent IDs in
  `src/Commands/NewEntitySyncPlanCommand.cs`).
- T031 done: added an `isLcatTarget` flag (`TargetVendor.Equals("LCAT", OrdinalIgnoreCase)`, computed once
  in `EndProcessing` after alias normalization) threaded through `MatchSources`/`CreatePlanItem` in
  `src/Commands/NewEntitySyncPlanCommand.cs`, plus a new check in `CreatePlanItem` — right after the
  existing `HaloNCentralIntegrationConflict` check, before any candidate matching — that blocks with
  `Action 'Review'`/`MatchType 'LcatSiteParentMissing'` and a reason mentioning "parent N-central customer
  identifier" whenever `isLcatTarget` is true, `source.EntityType == "Site"`, and
  `source.GetExternalId("NCentralCustomerId")` is null/whitespace (no fallback to `source.Id`, since that
  external id is the site's own identifier, not its parent's, per data-model.md). Deliberately checked
  `source.EntityType` (the actual entity on each pipeline/fetched source) rather than the requested
  `sourceEntityType` dynamic-parameter value, matching T030's mapper precedent, since pipeline sources
  bypass query-based entity-type filtering entirely. Placed the check ahead of matching (not folded into
  the existing `NoMatch` branch) so it fires regardless of `-CreateMissing` — this is what the T027 tests
  require ("blocks... even without -CreateMissing"). Updating this static method's signature required
  updating three pre-existing reflection-based tests in `Tests/LISSTech.EntitySync.Tests.ps1` (lines
  ~995, ~1016, ~1070 — "Does not treat ordinary NetSuite external IDs...", "Flags missing authoritative
  targets...", "Leaves low-confidence targets blank...") to pass an extra trailing `$false` argument;
  none of their assertions changed since none exercise an LCAT Site source. `just build` succeeds; `just
  test` now reports all 74 tests passing (0 failed) — both previously-red T027 tests pass with no
  regressions. Next incomplete task: T032 (include site-derived customer-scope items in the LCAT batch
  apply path in `src/Commands/InvokeEntitySyncPlanCommand.cs`).
- T032 assessed as already satisfied: `ApplyLcatBatch` in `src/Commands/InvokeEntitySyncPlanCommand.cs`
  (added in T024) has never branched on `item.Source.EntityType` — it maps every non-`None`/`Review`
  item via `mapper.MapCreate`/`MapUpdate` and converts the result through `ToLcatCustomerScopeRequest`
  into the same `batchItems` list regardless of whether the source is an NCentral Customer or Site, so
  once T030 taught `DefaultEntityMapper.AddLcatCustomerScopeFields` to populate Site fields
  (`ncentral_customer_id`/`ncentral_parent_customer_id`/`slug`), site-derived scopes started flowing
  through the existing batch path automatically with no command-side change needed. Confirmed via the
  existing T029 test ("Composes an LCAT batch payload carrying the site's own id and parent customer id
  alongside a customer item") at `Tests/LISSTech.EntitySync.Tests.ps1:783`, which reflects on the exact
  production `InvokeEntitySyncPlanCommand.ToLcatCustomerScopeRequest` helper (not a test-only
  reimplementation) for both a Customer and a Site request and asserts `BuildSyncRequestBody` serializes
  both into one `customers[]` array with the site's own id as `ncentral_customer_id` and the parent's id
  as `ncentral_parent_customer_id` — this already passes (has passed since T030, no changes in T031).
  Also traced that Review items (including T031's `LcatSiteParentMissing` block for sites with no
  parent) are already excluded from the batch by the pre-existing `Action.Equals("Review", ...)`
  short-circuit at the top of the per-item loop, so a site blocked for missing-parent never reaches
  `ToLcatCustomerScopeRequest` in the first place — no additional filtering required. No code change
  made for T032; `git log` confirms `InvokeEntitySyncPlanCommand.cs` has had no commits since T024.
  `just build` and `just test` both pass (74/74, unchanged). Next incomplete task: T033 (LCAT batch
  request must validate unique `ncentral_customer_id` values across customer and site items in
  `src/Adapters/LCAT/LCATEntityAdapter.cs`).
- T033 done: added a private static `EnsureUniqueCustomerIds` helper to
  `src/Adapters/LCAT/LCATEntityAdapter.cs`, called at the top of `SyncCustomerScopesAsync` before
  `BuildSyncRequestBody`/any HTTP call, that groups the batch by `NCentralCustomerId` (ordinal) and
  throws `InvalidOperationException` naming the offending id(s) if any group has more than one
  member — enforcing `contracts/lcat-sync-rpc.md`'s "`ncentral_customer_id` is required and must be
  unique within the request" rule as defense-in-depth at the adapter boundary, independent of
  plan-time duplicate detection (that's T043/US3, not yet implemented). Chose an adapter-level guard
  rather than relying solely on a future plan-time check because `SyncCustomerScopesAsync` is the
  last point before the batch leaves the process, and a site with no `NCentralSiteId` already falls
  back to `source.Id` (T030) which coincidentally equals its parent customer's id in some malformed
  inputs — this check catches that case regardless of how the batch was assembled. Added one Pester
  test ("Rejects an LCAT batch sync request carrying duplicate ncentral_customer_id values...") to
  `Tests/LISSTech.EntitySync.Tests.ps1` (after the T029 batch-composition test, before `Declares
  object output for Get-EntitySyncConnection`) that constructs two `LCATCustomerScopeRequest` objects
  sharing the same `NCentralCustomerId` and asserts `SyncCustomerScopesAsync` throws before any
  network call — this is safe to test without HTTP mocking (this repo's established no-mocking-infra
  convention per the T003 note) since the validation throws synchronously ahead of
  `httpClient.PostAsync`. T033 has no dedicated test entry in `tasks.md` (unlike T026-T029's "Tests
  for User Story 2" block), but the check is trivially testable without network access, unlike T019/
  T020/T032 which relied on manual-only verification for adapter-shell work that does need a live
  HTTP round trip. `just build` succeeds; `just test` reports all 75 tests passing (0 failed, up from
  74 — no regressions). Next incomplete task: T034 (update site sync examples and parent relationship
  notes in `docs/New-EntitySyncPlan.md`, `docs/Invoke-EntitySyncPlan.md`, and `README.md`).
- T034 done: added an Example 5 to `docs/New-EntitySyncPlan.md` (NCentral Site -> LCAT Customer plan)
  documenting that site-derived scopes carry the site's own id as `ncentral_customer_id` and the
  parent's id as `ncentral_parent_customer_id`, and that a site with no parent is blocked
  `Action 'Review'`/`MatchType 'LcatSiteParentMissing'` even with `-CreateMissing` (T031's actual
  behavior); also noted in Example 4 that customer-derived scopes leave `ncentral_parent_customer_id`
  empty. Extended `docs/Invoke-EntitySyncPlan.md`'s "## NCentral to LCAT" section with a paragraph
  describing that Customer and Site sources batch together in one call, the missing-parent Review
  block keeps a site out of the batch entirely (never reaches `ApplyLcatBatch`, confirmed by the T032
  chronicle note), and the adapter rejects the whole batch if two items share an `ncentral_customer_id`
  (T033). Updated `README.md`'s `### LCAT (planned)` section to `### LCAT` with present-tense wording
  covering the `LTAC` alias, no-read-endpoint/no-target-candidates behavior, and the site
  parent-relationship/missing-parent Review block; also fixed two stale references left over from
  before LCAT (and NCentral) adapters existed: the Project Structure `Adapters/` comment (was
  "HaloPSA + NetSuite vendor IO (LCAT planned)", now lists all four adapters that actually exist under
  `src/Adapters/`) and the Status section's "Planned: N-central customers/sites -> LCAT customer
  scopes" line (now "Also implemented: ..."). Did not touch the full LCAT quickstart walkthrough in
  README — that's T047 (Phase 6 Polish), a separate task from this one's narrower "site sync examples
  and parent relationship notes" scope. `just build` succeeds; `just test` reports all 75 tests passing
  (0 failed, docs-only change, no regressions). Next incomplete task: T035 (Phase 5 / US3 — Pester
  tests proving `LCATBearerToken` is absent from connection objects and common error messages).
- T035 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T033
  duplicate-id test, before `Declares object output for Get-EntitySyncConnection`). One calls the
  real `Connect-EntitySyncVendor -Vendor LCAT` cmdlet with a fake secret bearer token (safe without
  HTTP-mocking infra, per the T003/T021 precedent: T021's chronicle note already established this
  path never makes an eager network round-trip) and asserts the returned `EntitySyncConnection` has
  no `LCATBearerToken`/`BearerToken` property, and that `Format-List * | Out-String` on both the
  connection and its `Adapter` never contains the secret value — this already passed with no product
  code changes, since `EntitySyncConnection` only carries `Vendor`/`Adapter` (`src/Runtime/EntitySyncConnection.cs`)
  and `LCATEntityAdapter` never exposes its private `LCATOptions` as a public property. The other test
  forces an HTTPS-validation failure (`-LCATBaseUrl 'http://...'`, an intentionally invalid scheme)
  while still supplying the secret bearer token, and asserts neither the caught exception's `Message`
  nor its full `Out-String` rendering contains the secret — covering the "common error messages" half
  of the task without needing a real HTTP round trip, since `ValidateAbsoluteHttpsUrl`/`Require` in
  `ConnectEntitySyncVendorCommand.cs` never echo parameter values back into thrown messages. Did not
  add a test for `SyncCustomerScopesAsync`'s non-success-response error message (that needs a live
  HTTP round trip and is T039's job, not this task) or for N-central registration-token exclusion
  (T036, a separate task). Both new tests passed immediately with no product code changes — T040/T041
  (the actual redaction *implementation* tasks) still lie ahead, but the current shape of
  `EntitySyncConnection`/`LCATEntityAdapter`/`ConnectEntitySyncVendorCommand` already satisfies what
  T035 pins down, mirroring the T013/T029(-first-test)/T032 precedent where a test locks in
  pre-existing correct behavior. `just build` succeeds; `just test` reports all 77 tests passing (0
  failed, up from 75 — no regressions). Next incomplete task: T036 (Pester tests proving N-central
  registration tokens are not mapped into LCAT requests).
- T036 done: added two Pester tests to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T035
  error-message test, before `Declares object output for Get-EntitySyncConnection`). Both give an
  NCentral `ExternalEntity` source (one Customer, one Site) a `CustomFields['NCentralRegistrationToken']`
  entry simulating an N-central agent registration/deployment secret, then call
  `DefaultEntityMapper.MapCreate` targeting `LCAT`/`Customer` and assert `request.Fields` never
  contains the token key or value and `request.CustomFields.Count` is `0` — confirming
  `AddLcatCustomerScopeFields` (T022/T030) only ever writes its four known keys
  (`display_name`/`ncentral_customer_id`/`ncentral_parent_customer_id`/`slug`) and never copies
  `source.CustomFields` wholesale, and that no other `targetVendor == "LCAT"`-gated helper in
  `DefaultEntityMapper.cs` exists to leak it. The Site test additionally reflects on
  `InvokeEntitySyncPlanCommand.ToLcatCustomerScopeRequest` (T024) and
  `LCATEntityAdapter.BuildSyncRequestBody` (T011/T015) — the same production helpers T029's test
  already exercises — to serialize the mapped request into the actual batch JSON and assert the
  registration token string does not appear anywhere in it, extending the "no secret leak" guarantee
  all the way to the wire payload, not just the intermediate `EntityWriteRequest`. Both tests passed
  immediately with no product code changes, matching the T013/T029/T032/T035 precedent where a test
  locks in pre-existing correct behavior rather than driving new implementation — confirmed via
  `git log` that none of `DefaultEntityMapper.cs`/`InvokeEntitySyncPlanCommand.cs`/
  `LCATEntityAdapter.cs` needed edits. `just build` succeeds; `just test` reports all 79 tests
  passing (0 failed, up from 77 — no regressions). Next incomplete task: T037 (Pester tests for
  `Invoke-EntitySyncPlan -WhatIf` producing no LCAT writes).
- T037 done: added one Pester test to `Tests/LISSTech.EntitySync.Tests.ps1` (after the T036
  registration-token tests, before `Declares object output for Get-EntitySyncConnection`) proving
  `Invoke-EntitySyncPlan -Apply -WhatIf -PassThru` performs no LCAT batch sync. Registers an LCAT
  adapter whose `HttpClient` is disposed *before* registration, so if the `ShouldProcess` guard in
  `InvokeEntitySyncPlanCommand.ApplyLcatBatch` (`src/Commands/InvokeEntitySyncPlanCommand.cs:131`)
  were ever bypassed, `SyncCustomerScopesAsync`'s `httpClient.PostAsync` call would throw
  `ObjectDisposedException` and the command would terminate — a deterministic, network-free proof
  that avoids relying on DNS failure against the `lcat.example.test` placeholder domain the way the
  existing T017 WhatIf tests do. Asserts both no exception and that `-PassThru` produced no output
  (`$results | Should -BeNullOrEmpty`), confirming the early `return` at line 131 fires before any
  `WriteResult` call for the batch. Hit one authoring bug while writing this: `@($results).Count`
  is `1` even when `$results` is `$null`, because wrapping `$null` in `@()` produces a one-element
  array containing `$null` — classic PowerShell gotcha. Switched to `Should -BeNullOrEmpty`, which
  handles `$null` correctly. No product code changes; this test locks in behavior that
  `ApplyLcatBatch`'s existing `ShouldProcess` call already provided, same precedent as T013/T029/
  T035/T036. Left `tasks.md`'s T037 checkbox unchecked per the read-only-specs rule (T030-T036 were
  also never checked off there; this file is the actual progress record). `just build` succeeds;
  `just test` reports all 80 tests passing (0 failed, up from 79 — no regressions). Next incomplete
  task: T038 (Pester tests for Review, Reject, No Update, None, unsafe, duplicate, and incomplete
  items being skipped).
- T038 done: added one Pester test to `Tests/LISSTech.EntitySync.Tests.ps1` proving
  `Invoke-EntitySyncPlan -Plan <LCAT plan> -Apply -PassThru` skips Review, Reject, No Update, None,
  unsafe, duplicate, and incomplete items without attempting an LCAT batch write. The test registers
  a disposed LCAT adapter so any accidental approved batch call would fail deterministically, then
  builds only non-approved or invalid `EntitySyncPlanItem` rows: Review items are reported back as
  failed review results, while Reject/No Update/plain None rows remain silent skips. No product code
  changes were required because `ApplyLcatBatch` already skips `Action = None` and reports
  `Action = Review` before batch composition. While authoring the test, the first run failed because
  output assigned inside a `Should -Not -Throw` scriptblock was not available to the later
  assertions; the final test captures output directly and lets exceptions propagate. A parallel
  `just build`/`just test` attempt also hit a transient .NET `obj` cache file lock, so validation was
  rerun sequentially. Final validation: `just build` succeeds; `just test` reports all 81 tests
  passing (0 failed, up from 80). Next incomplete task: T039 (Pester tests for LCAT non-success
  responses returning status/path without authorization headers or credentials).
- T039 done: added a one-shot loopback HTTP server helper in `Tests/LISSTech.EntitySync.Tests.ps1`
  and a Pester test proving `LCATEntityAdapter.SyncCustomerScopesAsync` reports a controlled
  non-success response as `HTTP 403 Forbidden` with `Path: rpc/sync_ncentral_customers` while omitting
  the bearer token, `Authorization` header text, and response body details from the thrown error and
  rendered error record. The test also asserts the local server received the real POST and bearer
  header, so the redaction coverage exercises the actual HTTP path rather than a mocked exception.
  No product code changes were required because the existing adapter error message already used the
  desired status/path-only shape. `just build` succeeds; `just test` reports all 82 tests passing
  (0 failed, up from 81). Next incomplete task: T040 (redact LCAT authorization data from adapter
  exceptions and write results in `src/Adapters/LCAT/LCATEntityAdapter.cs`), which may already be
  partially satisfied by the T039-proven error shape but still needs an implementation-state check.
- T040 done: hardened `src/Adapters/LCAT/LCATEntityAdapter.cs` so LCAT transport failures from
  `TestConnectionAsync` and `SyncCustomerScopesAsync` are converted to fixed `InvalidOperationException`
  messages that include only the operation and RPC path, never authorization headers, bearer tokens,
  request JSON, or response body text. Preserved cancellation passthrough and existing status/path-only
  non-success HTTP reporting; moved successful response body reading after the status check so failure
  bodies are not consumed for error construction. No `EntityWriteResult` secret path exists in the
  adapter itself: LCAT batch apply results are composed in `InvokeEntitySyncPlanCommand.cs` from counts
  returned by `LCATSyncResult`, which carries no credential-bearing fields. `just build` succeeds;
  `just test` reports all 82 tests passing. Next incomplete task: T041 (ensure LCAT connection output
  does not expose credential-bearing options in `src/Commands/ConnectEntitySyncVendorCommand.cs`).
- T041 done: added a Pester test to `Tests/LISSTech.EntitySync.Tests.ps1` proving registered LCAT
  connection output from `Get-EntitySyncConnection` does not expose `LCATOptions`, `LCATBearerToken`,
  `BearerToken`, or the supplied bearer token value through normal property inspection,
  `Format-List *`, or `ConvertTo-Json -Depth 5`. No product code changes were required because
  `ConnectEntitySyncVendorCommand` already writes the shared `EntitySyncConnection` shape and
  `LCATEntityAdapter` keeps its `LCATOptions` field private. `just build` succeeds; `just test`
  reports all 83 tests passing (0 failed, up from 82). Next incomplete task: T042 (filter
  non-approved and invalid LCAT plan items before batch composition in
  `src/Commands/InvokeEntitySyncPlanCommand.cs`).
- T042 done: updated `src/Commands/InvokeEntitySyncPlanCommand.cs` so the LCAT apply path only
  composes approved `Create`/`Update`/`Link` items into the authoritative batch, validates required
  LCAT customer-scope fields before the adapter call, skips duplicate `ncentral_customer_id` values
  before they can fail the whole batch, and reports invalid approved items as failed non-secret
  pass-through results. Extended the loopback HTTP test helper in `Tests/LISSTech.EntitySync.Tests.ps1`
  to capture request bodies, then added a regression test proving one valid approved item is still
  sent while an empty-ID item and a duplicate-ID pair are filtered out before request composition.
  Initial test run exposed the need to pass `-Confirm:$false` for a real apply in the noninteractive
  Pester host; after that correction, `just test` rebuilt successfully and reports all 84 tests
  passing. Next incomplete task: T043 (unsafe slug, duplicate source ID, empty display name, and
  empty source ID safe-failure reasons during mapping/planning).
- T043 done: updated `src/Commands/NewEntitySyncPlanCommand.cs` so LCAT planning precomputes
  duplicate N-central source identifiers and marks invalid source records as `Review` with
  `LcatSourceInvalid` before normal matching can turn them into approved creates. The new validation
  covers missing source identifiers, duplicate source identifiers, empty display names, missing site
  parent customer identifiers, and source records that cannot produce a safe LCAT customer-scope
  slug; it reuses the LCAT slug contract helpers from `src/Mapping/DefaultEntityMapper.cs`. Added a
  Pester regression in `Tests/LISSTech.EntitySync.Tests.ps1` covering the new plan-time reasons and
  adjusted existing private-method tests for the new duplicate-ID input. Validation: `just build`
  succeeds; `just test` reports all 85 tests passing.
- T044 done: added `EntitySyncPlanArtifactSanitizer` in `src/Core/` and routed both workbook
  writes (`EntitySyncPlanWorkbook.Write`, including chain callers) and JSON exports
  (`ExportEntitySyncPlanCommand`) through a sanitized clone before serializing. The sanitizer removes
  credential-bearing `ExternalIds`/`CustomFields` entries and redacts explicitly credential-labeled
  plan reasons while preserving ordinary LCAT safe-failure reasons. Added a Pester regression proving
  LCAT `LcatSourceInvalid` reasons survive an XLSX export/import round trip, JSON export omits
  `LCATBearerToken`/`Authorization` and the secret value, normal N-central IDs/parent context remain,
  and export does not mutate the caller's in-memory plan. Validation: `just build` succeeds;
  `just test` reports all 86 tests passing. Next incomplete task: T045 (document credential handling
  and safety guarantees in `docs/Connect-EntitySyncVendor.md`, `docs/Invoke-EntitySyncPlan.md`, and
  `README.md`).
- T045 done: documented LCAT credential handling and safety guarantees in
  `docs/Connect-EntitySyncVendor.md`, `docs/Invoke-EntitySyncPlan.md`, and `README.md`. The docs now
  state that LCAT bearer tokens are connection-only secrets, are omitted from connection output,
  plan artifacts, result messages, and common adapter errors, and are not copied into LCAT batch
  payloads alongside unrelated N-central registration tokens. The apply docs now spell out that only
  approved, validated `Create`/`Update`/`Link` rows enter the authoritative LCAT batch while
  review-blocked, rejected, no-update, unsafe, incomplete, and duplicate-source rows are skipped or
  returned as non-secret failures. Validation: `just build` succeeds; `just test` reports all 86
  tests passing. Next incomplete task: T046 (update `docs/Get-EntitySyncEntity.md` with LCAT Customer
  read behavior and empty-target fallback notes).
- T046 done: updated `docs/Get-EntitySyncEntity.md` with current LCAT Customer read behavior and
  empty-target fallback notes: `LCAT`/`LTAC` support only `Customer`, LCAT currently has no
  customer-scope read surface, and reads return an empty set so N-central sources still plan as
  customer-scope sync candidates without fabricating target candidates. While verifying the docs,
  found and removed a stale `NotImplementedException` guard in `src/Commands/GetEntitySyncEntityCommand.cs`
  so the public command now reaches `LCATEntityAdapter.GetEntitiesAsync` and matches the contract.
  Added a Pester regression for `Get-EntitySyncEntity -Vendor LCAT|LTAC -Type Customer` returning
  empty output. Validation: `just build`, `just test-load`, and `just test` all succeed; `just test`
  reports all 87 tests passing. Next incomplete task: T047 (add LCAT quickstart examples to
  `README.md`).
- T047 done: added LCAT quickstart examples to `README.md` covering N-central Customer to LCAT
  customer-scope planning, workbook export/import review, `-WhatIf -PassThru` dry-run, Site to LCAT
  dry-run with parent-customer context, and final reviewed apply as one authoritative LCAT batch.
  Validation: `just build` succeeds; `just test` reports all 87 tests passing. Next incomplete task:
  T048 (review LCAT spec artifacts for implementation drift).
- T048 done: reviewed `specs/001-lcat-sync-adapter/plan.md`,
  `specs/001-lcat-sync-adapter/contracts/lcat-sync-rpc.md`, and
  `specs/001-lcat-sync-adapter/quickstart.md` against the implemented LCAT command, mapping, and
  adapter paths. The listed spec artifacts still match the current batch contract: `POST
  /rpc/sync_ncentral_customers`, bearer auth outside the JSON body, `LCAT_BASE_URL`/
  `LCAT_BEARER_TOKEN` environment fallbacks, customer/site fields, aggregate response counts, and
  dry-run/apply semantics. Specs are read-only, so no spec files were edited. While reviewing, found
  one source-level drift item outside the T048 artifact list: `Test-EntitySyncConnection -Vendor
  LCAT` still throws the old not-implemented guard even though `LCATEntityAdapter.TestConnectionAsync`
  exists and the PowerShell command contract includes LCAT connection testing. Validation:
  `just build` succeeds. Next incomplete task: T049 (`just build`) or resolve the open finding below
  before final polish validation.

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|
| P2 | `Test-EntitySyncConnection -Vendor LCAT` still throws `NotImplementedException` instead of using the registered LCAT adapter. | `src/Commands/TestEntitySyncConnectionCommand.cs` has an explicit LCAT guard; `src/Adapters/LCAT/LCATEntityAdapter.cs` now implements `TestConnectionAsync`; `specs/001-lcat-sync-adapter/contracts/powershell-command-contract.md` documents `Test-EntitySyncConnection -Vendor LCAT`. | Open; remove the stale guard and add a regression before final polish validation. |

## Decisions To Preserve

-
