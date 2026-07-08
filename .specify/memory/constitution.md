<!--
Sync Impact Report
Version change: unversioned template -> 1.0.0
Modified principles:
- Template principle 1 placeholder -> I. Safe Plan-First Synchronization
- Template principle 2 placeholder -> II. Canonical Core, Adapter Edges
- Template principle 3 placeholder -> III. Explainable Matching and Operator Review
- Template principle 4 placeholder -> IV. PowerShell Module Contract
- Template principle 5 placeholder -> V. Test and Build Gates
Added sections:
- Operational Constraints
- Development Workflow
- Governance
Removed sections:
- Template placeholder comments and undefined placeholder sections
Templates requiring updates:
- .specify/templates/plan-template.md: ✅ updated
- .specify/templates/spec-template.md: ✅ updated
- .specify/templates/tasks-template.md: ✅ updated
- .specify/templates/commands/*.md: ⚠ pending (directory absent)
Follow-up TODOs: None
-->

# LISSTech.EntitySync Constitution

## Core Principles

### I. Safe Plan-First Synchronization

Vendor discovery and plan generation MUST NOT mutate vendor systems. Any vendor write MUST be
represented in an `EntitySyncPlan`, reviewed as an explicit action, and executed only through an
apply path that requires `-Apply` and honors `-WhatIf`. Items marked `Review` or rejected by an
operator MUST be skipped during apply. Rationale: vendor entity data is expensive to repair, so the
system must make change intent visible before any mutation is possible.

### II. Canonical Core, Adapter Edges

Canonical entity models, matching, sync plans, and safe application rules MUST remain in the core
module. Vendor-specific API calls, payload shapes, paging, authentication, and field translation
MUST live behind adapter boundaries under `src/Adapters` and port abstractions. New vendor support
MUST map into canonical models before matching or planning. Rationale: keeping vendor behavior at
the edge prevents one integration from contaminating shared safety and matching logic.

### III. Explainable Matching and Operator Review

Every non-trivial match decision MUST include a score, the evidence used, and human-readable
reasons that survive export, import, dry-run, and apply output. Ambiguous matches, duplicate target
names, conflicting reviewer choices, missing authoritative IDs, or scores below configured
automatic thresholds MUST fail safely or become `Review`; the module MUST NOT guess silently.
Rationale: operators need traceable evidence to trust, correct, or reject risky sync decisions.

### IV. PowerShell Module Contract

Public behavior MUST be exposed through PowerShell 7.4+ cmdlets in the binary module, using
pipeline-friendly objects, explicit parameters, and standard PowerShell safety semantics where
writes are possible. The module manifest in `Module/` MUST import cleanly after build, exported
commands MUST remain documented, and generated help in `docs/` / `en-US/` MUST be updated when
public parameters or behavior change. Rationale: predictable PowerShell ergonomics are the
operator interface and the compatibility contract for this module.

### V. Test and Build Gates

Behavior changes MUST include Pester coverage for core logic, command behavior, and adapter
contract expectations affected by the change. Matching, planning, import/export round trips, and
apply safety paths require tests for both success and safe-failure cases. `just build` and relevant
Pester tests MUST pass before merge; `just test-load` MUST pass when public commands, manifests,
or help assets change. Rationale: the module's safety model depends on regressions being caught
before operators run it against real vendors.

## Operational Constraints

The supported runtime is PowerShell 7.4+ on .NET 8. Secrets MAY be supplied by parameters or
environment variables, but secrets MUST NOT be written to exported plans, help examples, logs, or
test fixtures. The required operator workflow is inspect, plan, review, dry run, then apply.
Generated plans and review workbooks are safety artifacts and MUST preserve enough context to
round-trip reviewer decisions without losing match evidence. Vendor-specific fallback behavior is
allowed only when it preserves correctness and fails safely when required evidence is unavailable.

## Development Workflow

Feature specs and plans MUST identify whether the change can write to a vendor, changes canonical
models, changes adapter behavior, changes match scoring, or changes exported artifacts. Plans MUST
state the affected safety gates, test coverage, and documentation updates before implementation.
Tasks MUST keep foundational safety work separate from user-story implementation and MUST include
validation tasks for build, module load, tests, and documentation whenever those areas are touched.
Complexity that weakens the adapter boundary, bypasses plan review, or reduces match explainability
MUST be documented with a safer alternative and an explicit reason it was rejected.

## Governance

This constitution supersedes conflicting development practices for LISSTech.EntitySync. Amendments
MUST be made by updating this file, including a Sync Impact Report, and propagating any changed
rules to Spec Kit templates and runtime guidance in the same change. Each amendment MUST document
the version change, affected principles, migration impact, and follow-up work.

Versioning follows semantic versioning. MAJOR versions are required for removals or incompatible
redefinitions of principles or governance rules. MINOR versions are required for new principles,
new mandatory sections, or materially expanded compliance guidance. PATCH versions are used for
clarifications, wording fixes, and non-semantic refinements.

All feature plans and code reviews MUST verify compliance with the Core Principles. Any temporary
exception MUST be recorded in the plan's Complexity Tracking section with a remediation path and
reviewed before merge. Release-ready work MUST pass the applicable build, module-load, Pester, and
documentation gates defined by this constitution.

**Version**: 1.0.0 | **Ratified**: 2026-07-08 | **Last Amended**: 2026-07-08
