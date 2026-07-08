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
  and `just test` both pass (51/51). Next incomplete task: T012 (slug
  validation helper for LCAT customer scopes in
  `src/Mapping/DefaultEntityMapper.cs`).

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|

## Decisions To Preserve

-
