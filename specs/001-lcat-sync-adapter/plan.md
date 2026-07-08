# Implementation Plan: LCAT Customer Scope Sync

**Branch**: `001-lcat-sync-adapter` | **Date**: 2026-07-08 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-lcat-sync-adapter/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Add LCAT as a safe EntitySync target for N-central customer and site records. The feature will add
LCAT connection support, map N-central customer/site sources into LCAT customer-scope sync items,
generate reviewable plans without writes, and apply approved N-central-to-LCAT items as one
authoritative batch so LCAT owns upsert and retirement behavior.

## Technical Context

**Language/Version**: C# latest on .NET 8, PowerShell 7.4+ binary module

**Primary Dependencies**: `System.Management.Automation` 7.4.6, `HttpClient`, `System.Text.Json`,
existing EntitySync adapter/port abstractions, Pester, just

**Storage**: No local persistence; plans and review workbooks remain file-based artifacts. LCAT owns
customer-scope state and retirement behavior.

**Testing**: Pester tests in `Tests/`; validation through `just build`, `just test-load`, `just test`

**Target Platform**: PowerShell 7.4+ on .NET 8 wherever the binary module runs

**Project Type**: PowerShell binary module with C# adapter, command, mapping, and core model code

**Performance Goals**: Apply at least 1,000 combined N-central customer/site records in one operator
run using a single LCAT batch sync request.

**Constraints**: Discovery and planning stay read-only; LCAT writes require `-Apply` and honor
`-WhatIf`; approved LCAT items are applied as one authoritative batch; credentials and N-central
registration tokens are never serialized, logged, returned, or sent to LCAT.

**Scale/Scope**: One new target vendor (`LCAT`) with target entity `Customer`; source flows are
NCentral Customer -> LCAT Customer and NCentral Site -> LCAT Customer.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Safe plan-first synchronization**: PASS. Discovery and planning remain read-only; LCAT writes
  occur only from reviewed plans through `Invoke-EntitySyncPlan -Apply`, and `-WhatIf` remains the
  dry-run boundary. LCAT-specific apply batches only approved `Create`/`Update`/`Link`-equivalent
  items and skips `Review`, `Reject`, `No Update`, and `None` decisions.
- **Canonical core / adapter edge**: PASS. LCAT HTTP behavior and payload translation live in
  `src/Adapters/LCAT`; command registration and plan/apply orchestration use existing ports,
  canonical `ExternalEntity`, `EntitySyncPlan`, and `EntityWriteRequest` models.
- **Explainable matching**: PASS. Plans retain source evidence, target candidates where available,
  action decisions, and safe-failure reasons for unsafe slugs, duplicate identifiers, missing parent
  IDs, and LCAT failures through export/import/dry-run/apply output.
- **PowerShell module contract**: PASS. Public behavior is exposed through existing cmdlets plus
  LCAT dynamic parameters and validation; help docs and manifest exports must be updated where the
  public surface changes.
- **Test and build gates**: PASS. Pester coverage is required for vendor completion, LCAT entity
  support, mapping, credential redaction, batch apply behavior, safe failures, and validation gates.

Post-design re-check: PASS. Phase 1 artifacts preserve the same constraints and introduce no
unjustified constitution violations.

## Project Structure

### Documentation (this feature)

```text
specs/001-lcat-sync-adapter/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── lcat-sync-rpc.md
│   └── powershell-command-contract.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/
├── Adapters/
│   └── LCAT/          # new LCAT options and entity adapter
├── Commands/          # vendor validation, plan creation, and batch apply integration
├── Core/              # shared plan/write result models if batch results need representation
├── Mapping/           # N-central customer/site to LCAT customer-scope request mapping
├── Matching/          # existing explainable matching; no new matcher required
├── Ports/             # existing adapter contracts, extended only if batch flush is required
└── Runtime/           # existing connection registry

Tests/
└── LISSTech.EntitySync.Tests.ps1

docs/                  # command help markdown updates
Module/                # compiled module manifest surface
```

**Structure Decision**: Implement LCAT as another vendor adapter under `src/Adapters/LCAT` and keep
shared safety behavior in existing commands/core models. Prefer an LCAT-specific batch branch in
`InvokeEntitySyncPlanCommand` over per-item adapter writes because LCAT's sync operation is
authoritative for the full N-central-sourced set.

## Complexity Tracking

No constitution violations identified. The LCAT-specific batch apply path is required by the target
system's authoritative full-sync semantics and is the simpler safe alternative to buffering per-item
adapter writes.
