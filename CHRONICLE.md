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

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|

## Decisions To Preserve

-
