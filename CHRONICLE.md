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

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|

## Decisions To Preserve

-
