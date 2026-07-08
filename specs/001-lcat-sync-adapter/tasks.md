# Tasks: LCAT Customer Scope Sync

**Input**: Design documents from `/specs/001-lcat-sync-adapter/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Pester tests are REQUIRED by the feature plan and constitution for command surface,
adapter contract, mapping, batch apply, safe-failure, and credential-redaction behavior.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Module source**: `src/Commands/`, `src/Core/`, `src/Adapters/`, `src/Ports/`, `src/Mapping/`, `src/Matching/`, `src/Runtime/`
- **Tests**: `Tests/LISSTech.EntitySync.Tests.ps1`
- **Documentation**: `docs/`, `README.md`, `Module/LISSTech.EntitySync.psd1`
- **Feature docs**: `specs/001-lcat-sync-adapter/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare LCAT-specific source, documentation, and test locations before behavior work.

- [ ] T001 Create LCAT adapter directory and placeholder files in `src/Adapters/LCAT/LCATOptions.cs` and `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T002 [P] Add LCAT documentation stubs to `docs/Connect-EntitySyncVendor.md`, `docs/Get-EntitySyncEntity.md`, `docs/New-EntitySyncPlan.md`, and `docs/Invoke-EntitySyncPlan.md`
- [ ] T003 [P] Add LCAT test context scaffolding comments and helper region in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T004 [P] Add LCAT feature references to `README.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared vendor registration, entity-type routing, and contracts needed by all stories.

**CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T005 Add `LCAT` and optional `LTAC` alias normalization helpers in `src/Commands/ConnectEntitySyncVendorCommand.cs`
- [ ] T006 Add `LCAT` vendor validation and Customer-only entity type completion in `src/Commands/GetEntitySyncEntityCommand.cs`
- [ ] T007 Add `LCAT` target vendor validation and Customer-only target entity type completion in `src/Commands/NewEntitySyncPlanCommand.cs`
- [ ] T008 Add `LCAT` vendor support to connection testing in `src/Commands/TestEntitySyncConnectionCommand.cs`
- [ ] T009 Add LCAT lookup behavior as an empty lookup set in `src/Core/EntitySyncLookupTypes.cs`
- [ ] T010 Add LCAT command export and tag metadata updates in `Module/LISSTech.EntitySync.psd1`
- [ ] T011 Add shared LCAT request/response model support in `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T012 Add slug validation helper for LCAT customer scopes in `src/Mapping/DefaultEntityMapper.cs`

**Checkpoint**: LCAT is recognized by the command surface and has a target-only adapter shell.

---

## Phase 3: User Story 1 - Sync N-central Customers to LCAT (Priority: P1) MVP

**Goal**: Operators can plan, dry-run, and apply approved N-central customer records into active LCAT customer scopes as one authoritative batch.

**Independent Test**: Prepare a reviewed plan containing approved N-central customer records, run `Invoke-EntitySyncPlan -Apply -WhatIf`, then apply and verify the LCAT batch contains all approved customers and reports inserted/updated/retired/active counts.

### Tests for User Story 1

- [ ] T013 [P] [US1] Add Pester tests for `LCAT` vendor completion and Customer-only target entity completion in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T014 [P] [US1] Add Pester tests for NCentral Customer to LCAT slug/display/id mapping in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T015 [P] [US1] Add Pester tests for LCAT adapter customer batch request and count response parsing in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T016 [P] [US1] Add Pester tests for NCentral Customer to LCAT plan creation without vendor writes in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T017 [P] [US1] Add Pester tests for `Invoke-EntitySyncPlan` sending one approved customer batch to LCAT in `Tests/LISSTech.EntitySync.Tests.ps1`

### Implementation for User Story 1

- [ ] T018 [US1] Implement LCAT connection options with base URL and bearer credential in `src/Adapters/LCAT/LCATOptions.cs`
- [ ] T019 [US1] Implement LCAT adapter connection validation, Customer reads, and no per-item create/update behavior in `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T020 [US1] Implement LCAT batch sync method for approved customer-scope requests in `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T021 [US1] Register `Connect-EntitySyncVendor -Vendor LCAT` dynamic parameters and environment fallbacks in `src/Commands/ConnectEntitySyncVendorCommand.cs`
- [ ] T022 [US1] Map NCentral Customer sources to LCAT Customer fields `slug`, `display_name`, `ncentral_customer_id`, and null parent in `src/Mapping/DefaultEntityMapper.cs`
- [ ] T023 [US1] Preserve LCAT target candidates and Customer-only target defaults during NCentral Customer planning in `src/Commands/NewEntitySyncPlanCommand.cs`
- [ ] T024 [US1] Add LCAT customer batch apply branch with `-Apply`, `-WhatIf`, `ShouldProcess`, and `-PassThru` support in `src/Commands/InvokeEntitySyncPlanCommand.cs`
- [ ] T025 [US1] Update public command help for customer sync flow in `docs/Connect-EntitySyncVendor.md`, `docs/New-EntitySyncPlan.md`, and `docs/Invoke-EntitySyncPlan.md`

**Checkpoint**: User Story 1 is fully functional and independently testable with customer-only N-central to LCAT sync.

---

## Phase 4: User Story 2 - Sync N-central Sites as LCAT Customer Scopes (Priority: P2)

**Goal**: Operators can plan, dry-run, and apply approved N-central site records into LCAT customer scopes while preserving parent N-central customer IDs.

**Independent Test**: Prepare a reviewed plan containing approved N-central site records, run a dry run, then apply and verify each site-derived LCAT customer scope includes its parent N-central customer identifier or is blocked with a clear reason.

### Tests for User Story 2

- [ ] T026 [P] [US2] Add Pester tests for NCentral Site to LCAT payload mapping with `ncentral_parent_customer_id` in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T027 [P] [US2] Add Pester tests for missing site parent N-central customer ID safe failure in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T028 [P] [US2] Add Pester tests for site-derived slug generation using parent customer and site names in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T029 [P] [US2] Add Pester tests for NCentral Site to LCAT plan creation and batch payload composition in `Tests/LISSTech.EntitySync.Tests.ps1`

### Implementation for User Story 2

- [ ] T030 [US2] Extend LCAT mapping for NCentral Site sources with site ID, display name, slug, and parent customer ID in `src/Mapping/DefaultEntityMapper.cs`
- [ ] T031 [US2] Add plan-time validation reasons for missing NCentral site parent IDs in `src/Commands/NewEntitySyncPlanCommand.cs`
- [ ] T032 [US2] Include site-derived customer-scope items in the LCAT batch apply path in `src/Commands/InvokeEntitySyncPlanCommand.cs`
- [ ] T033 [US2] Ensure LCAT batch request validates unique `ncentral_customer_id` values across customer and site items in `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T034 [US2] Update site sync examples and parent relationship notes in `docs/New-EntitySyncPlan.md`, `docs/Invoke-EntitySyncPlan.md`, and `README.md`

**Checkpoint**: User Story 2 is independently testable with N-central site records synced as LCAT customer scopes.

---

## Phase 5: User Story 3 - Protect Credentials and Plan Safety (Priority: P3)

**Goal**: Operators can connect, plan, dry-run, export/import, and apply LCAT sync plans without exposing credentials or allowing unreviewed writes.

**Independent Test**: Connect to LCAT using configured credentials, generate/export/import a plan, run dry-run and apply paths, inspect returned objects/artifacts/output, and verify credentials are absent while non-approved items are skipped.

### Tests for User Story 3

- [ ] T035 [P] [US3] Add Pester tests proving `LCATBearerToken` is absent from connection objects and common error messages in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T036 [P] [US3] Add Pester tests proving N-central registration tokens are not mapped into LCAT requests in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T037 [P] [US3] Add Pester tests for `Invoke-EntitySyncPlan -WhatIf` producing no LCAT writes in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T038 [P] [US3] Add Pester tests for Review, Reject, No Update, None, unsafe, duplicate, and incomplete items being skipped in `Tests/LISSTech.EntitySync.Tests.ps1`
- [ ] T039 [P] [US3] Add Pester tests for LCAT non-success responses returning status/path without authorization headers or credentials in `Tests/LISSTech.EntitySync.Tests.ps1`

### Implementation for User Story 3

- [ ] T040 [US3] Redact LCAT authorization data from adapter exceptions and write results in `src/Adapters/LCAT/LCATEntityAdapter.cs`
- [ ] T041 [US3] Ensure LCAT connection output does not expose credential-bearing options in `src/Commands/ConnectEntitySyncVendorCommand.cs`
- [ ] T042 [US3] Filter non-approved and invalid LCAT plan items before batch composition in `src/Commands/InvokeEntitySyncPlanCommand.cs`
- [ ] T043 [US3] Add unsafe slug, duplicate source ID, empty display name, and empty source ID safe-failure reasons in `src/Mapping/DefaultEntityMapper.cs` and `src/Commands/NewEntitySyncPlanCommand.cs`
- [ ] T044 [US3] Confirm plan export/import round trips LCAT reasons and excludes credentials in `src/Core/EntitySyncPlanWorkbook.cs` and `src/Commands/ExportEntitySyncPlanCommand.cs`
- [ ] T045 [US3] Document credential handling and safety guarantees in `docs/Connect-EntitySyncVendor.md`, `docs/Invoke-EntitySyncPlan.md`, and `README.md`

**Checkpoint**: User Story 3 is independently testable for credential redaction, dry-run safety, and non-approved item skipping.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, help generation, and cleanup across all LCAT stories.

- [ ] T046 [P] Update `docs/Get-EntitySyncEntity.md` with LCAT Customer read behavior and empty-target fallback notes
- [ ] T047 [P] Add LCAT quickstart examples to `README.md`
- [ ] T048 [P] Review LCAT spec artifacts for implementation drift in `specs/001-lcat-sync-adapter/plan.md`, `specs/001-lcat-sync-adapter/contracts/lcat-sync-rpc.md`, and `specs/001-lcat-sync-adapter/quickstart.md`
- [ ] T049 Run `just build` using `justfile`
- [ ] T050 Run `just test-load` using `justfile`
- [ ] T051 Run `just test` using `justfile`
- [ ] T052 Run LCAT quickstart dry-run validation from `specs/001-lcat-sync-adapter/quickstart.md`
- [ ] T053 Regenerate external help from `docs/` into `en-US/` using `justfile` if platyPS is available

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies; can start immediately.
- **Foundational (Phase 2)**: Depends on Setup; blocks all user stories.
- **User Story 1 (Phase 3)**: Depends on Foundational; delivers MVP customer sync.
- **User Story 2 (Phase 4)**: Depends on Foundational and can reuse US1 LCAT batch infrastructure; independently testable with site sources.
- **User Story 3 (Phase 5)**: Depends on Foundational and touches both customer/site apply paths; can run after or alongside US1/US2 once their files are coordinated.
- **Polish (Phase 6)**: Depends on completed desired user stories.

### User Story Dependencies

- **US1**: Must be completed first for MVP and shared LCAT batch apply infrastructure.
- **US2**: Requires the LCAT adapter and batch infrastructure from Foundational/US1 but remains independently validated with site inputs.
- **US3**: Requires LCAT connection/apply paths and can be validated after US1 for customer-only safety, then expanded after US2 for site safety.

### Within Each User Story

- Write Pester tests before implementation tasks and confirm they fail for missing behavior.
- Implement mapping before plan/apply integration that consumes mapped fields.
- Implement adapter batch behavior before command apply wiring.
- Update documentation after public behavior is implemented.
- Validate each story at its checkpoint before moving to the next priority.

### Parallel Opportunities

- T002, T003, and T004 can run in parallel after T001.
- T013 through T017 can run in parallel because they add separate Pester coverage areas in the same file with coordination.
- T018 through T020 can run in parallel with T021 through T023 after Foundational if file ownership is coordinated.
- T026 through T029 can run in parallel because each covers a distinct site-sync behavior.
- T035 through T039 can run in parallel because each covers a distinct safety behavior.
- T046 through T048 can run in parallel after story documentation stabilizes.

---

## Parallel Example: User Story 1

```bash
Task: "T013 [US1] Add Pester tests for LCAT vendor completion in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T014 [US1] Add Pester tests for NCentral Customer to LCAT mapping in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T015 [US1] Add Pester tests for LCAT adapter batch request parsing in Tests/LISSTech.EntitySync.Tests.ps1"
```

## Parallel Example: User Story 2

```bash
Task: "T026 [US2] Add Pester tests for site payload mapping in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T027 [US2] Add Pester tests for missing parent safe failure in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T028 [US2] Add Pester tests for site-derived slug generation in Tests/LISSTech.EntitySync.Tests.ps1"
```

## Parallel Example: User Story 3

```bash
Task: "T035 [US3] Add Pester tests for credential redaction in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T037 [US3] Add Pester tests for WhatIf no-write behavior in Tests/LISSTech.EntitySync.Tests.ps1"
Task: "T039 [US3] Add Pester tests for non-secret LCAT failure messages in Tests/LISSTech.EntitySync.Tests.ps1"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational vendor and adapter shell work.
3. Complete Phase 3: N-central Customer -> LCAT Customer sync.
4. Stop and validate customer plan dry-run/apply behavior with Pester plus quickstart customer flow.

### Incremental Delivery

1. Deliver US1 customer sync as the MVP.
2. Add US2 site-derived customer scopes with parent preservation.
3. Add US3 hardening for credential redaction, dry-run behavior, and skipped non-approved items.
4. Run all validation gates and update docs/help.

### Parallel Team Strategy

With multiple developers:

1. One developer owns `src/Adapters/LCAT/` batch behavior.
2. One developer owns `src/Commands/` vendor registration, plan, and apply wiring.
3. One developer owns `Tests/LISSTech.EntitySync.Tests.ps1` and docs updates.
4. Coordinate changes to `DefaultEntityMapper.cs` because all stories touch LCAT mapping.

---

## Notes

- [P] tasks = different files or distinct test sections with no dependency on incomplete implementation.
- [US1], [US2], and [US3] labels map directly to prioritized user stories in `spec.md`.
- Every behavior task includes an exact repository path.
- Required tests must be written before implementation and fail for the missing behavior.
- Avoid per-item LCAT write implementations; normal apply must batch approved items once per reviewed plan.
