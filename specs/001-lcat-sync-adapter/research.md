# Research: LCAT Customer Scope Sync

## Decision: Implement LCAT as a target-only EntitySync adapter

**Rationale**: The feature requires LCAT as a downstream materialization target for N-central
customer and site records. Keeping LCAT behavior in a dedicated adapter preserves the canonical core
and adapter-edge boundary required by the constitution.

**Alternatives considered**: Embedding LCAT HTTP calls directly in command classes was rejected
because it would smear vendor behavior through the safe planning/apply orchestration. Extending the
N-central adapter was rejected because the LCAT behavior belongs to the target system, not the
source read path.

## Decision: Apply approved LCAT items in one batch per reviewed plan

**Rationale**: LCAT's N-central sync contract is authoritative and can retire rows missing from the
payload. Normal per-item writes would make each item look like a full sync and could retire every
other synced row. A single batch preserves intended full-sync semantics and plan-first safety.

**Alternatives considered**: Buffering adapter writes behind `CreateEntityAsync`/`UpdateEntityAsync`
was considered but rejected as less explicit and harder to reason about. Per-item RPC calls were
rejected because they violate the source contract and risk data loss.

## Decision: Use PostgREST JSON object keys matching the LCAT sync contract

**Rationale**: Current PostgREST documentation shows named function arguments are passed as JSON
object keys that match the function parameter names. The feature request specifies the LCAT request
body with `customers`, `reason`, and `ticket`; the contract will use those names unless the upstream
AgentController contract changes.

**Alternatives considered**: Prefixing keys as `p_customers`, `p_reason`, and `p_ticket` was
rejected because the latest feature source corrected the payload to unprefixed names. A single
unnamed JSON argument was rejected because the supplied contract describes named payload fields.

## Decision: Represent LCAT targets as canonical `ExternalEntity` customer scopes

**Rationale**: Planning and matching already operate on canonical entities. LCAT customer scopes can
be represented with vendor `LCAT`, entity type `Customer`, source identifiers in `ExternalIds`, and
sync metadata in custom fields while keeping matching and review behavior consistent.

**Alternatives considered**: Adding a new core entity type was rejected because the canonical model
already carries the fields needed for identity, display name, custom metadata, and active/retired
state.

## Decision: Generate slugs in mapping with fail-safe validation

**Rationale**: LCAT requires safe customer-scope slugs. Mapping is the boundary where source records
become target write requests, so it can derive slugs from source names, parent names, and existing
source fields, then fail safely before apply if a slug is empty, unsafe, or conflicting.

**Alternatives considered**: Letting LCAT generate slugs was rejected because operators need
reviewable evidence before apply and duplicate/unsafe values must be visible in the plan. Storing
slug logic inside the N-central adapter was rejected because slug rules are target-specific.

## Decision: Redact and avoid credentials by construction

**Rationale**: LCAT credentials are privileged and must not appear in connection outputs, exported
plans, common errors, or tests. The adapter should store credentials only in options/private fields,
send them only as authorization material, and sanitize failure messages to status and endpoint path.

**Alternatives considered**: Returning full connection options for diagnostics was rejected because
it risks secret disclosure. Capturing request bodies in errors was rejected because payloads may
accumulate sensitive source metadata over time.

## Decision: Require Pester coverage and standard build/load/test gates

**Rationale**: The constitution requires Pester coverage for behavior changes and module-load
validation for public command changes. This feature changes vendor validation, dynamic parameters,
mapping, plan/apply behavior, and help docs.

**Alternatives considered**: Manual smoke testing alone was rejected because credential redaction,
safe-failure behavior, and batch application are regression-sensitive and must be automated.

## Agent Context Update

No agent context update script is installed under `.specify/scripts/powershell`, so no script was run.
Existing `AGENTS.md` already captures the relevant project scope: canonical models, explainable fuzzy
matching, sync plans, safe application, and vendor API behavior under `src/Adapters`.
