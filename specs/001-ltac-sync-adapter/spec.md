# Feature Specification: LTAC Customer Scope Sync

**Feature Branch**: `main`

**Created**: 2026-07-08

**Status**: Draft

**Input**: User description: "Implement LTAC Sync Adapter"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sync N-central Customers to LTAC (Priority: P1)

An operator needs to sync N-central customer records into LTAC customer scopes so LTAC reflects the
same customer population managed in N-central without manually creating customers in LTAC.

**Why this priority**: Customer-level sync is the primary business outcome and enables LTAC to use
N-central as the source of truth for customer scopes.

**Independent Test**: Prepare a reviewed plan containing approved N-central customer records, run a
dry run, then apply it and verify LTAC contains active customer scopes for the approved records with
the expected N-central identifiers.

**Acceptance Scenarios**:

1. **Given** approved N-central customer records and an authenticated LTAC connection, **When** the
   operator applies the reviewed sync plan, **Then** LTAC contains active customer scopes matching
   the approved N-central customers.
2. **Given** an N-central customer already synced to LTAC, **When** the customer's display name or
   source identifier is included in a later approved sync, **Then** the existing LTAC customer scope
   is updated rather than duplicated.
3. **Given** an N-central customer is omitted from a full approved sync payload, **When** the sync is
   applied, **Then** the previously synced LTAC customer scope is retired according to LTAC's
   authoritative sync behavior.

---

### User Story 2 - Sync N-central Sites as LTAC Customer Scopes (Priority: P2)

An operator needs N-central site records to appear as LTAC customer scopes while preserving the
relationship to their parent N-central customer.

**Why this priority**: Site-level scope support lets LTAC represent operational customer locations
without losing parent-customer context.

**Independent Test**: Prepare a reviewed plan containing approved N-central site records with parent
customer identifiers, run a dry run, then apply it and verify LTAC customer scopes are created or
updated with the correct parent relationship.

**Acceptance Scenarios**:

1. **Given** an approved N-central site with a known parent customer, **When** the operator applies
   the reviewed sync plan, **Then** LTAC contains a customer scope for the site and records the
   parent N-central customer relationship.
2. **Given** an approved N-central site whose parent customer identifier is missing, **When** the
   operator reviews the plan, **Then** the item is blocked for review or fails safely with a clear
   non-secret reason.

---

### User Story 3 - Protect Credentials and Plan Safety (Priority: P3)

An operator needs to connect LTAC with a privileged credential and run reviewable dry-run/apply flows
without exposing that credential or allowing unreviewed writes.

**Why this priority**: LTAC sync changes customer scope state and uses privileged credentials, so
safety and credential handling are required for production use.

**Independent Test**: Connect to LTAC using configured credentials, generate and export a sync plan,
run a dry run, inspect all returned objects and plan artifacts, and verify the credential is absent
while unapproved or review-blocked items are not applied.

**Acceptance Scenarios**:

1. **Given** an operator provides LTAC credentials, **When** connection, planning, export, dry run,
   or apply output is inspected, **Then** the credential value is not displayed or serialized.
2. **Given** a sync plan contains items marked for review, rejection, or no update, **When** the
   operator applies the plan, **Then** those items are skipped and no LTAC customer scope changes are
   made for them.
3. **Given** the operator runs the plan as a dry run, **When** the run completes, **Then** no LTAC
   customer scope state is changed and the operator can see which approved items would change.

---

### Edge Cases

- LTAC connection credentials are missing, expired, malformed, or unauthorized.
- A source customer or site cannot produce a safe customer-scope slug.
- Two source records would produce the same LTAC slug or N-central identifier.
- A site record lacks its parent N-central customer identifier.
- LTAC is unreachable or returns a partial, failed, or malformed sync result.
- A plan contains no approved N-central customer or site items.
- A full sync intentionally omits previously synced records that LTAC will retire.
- Plan export, import, dry-run, and apply output must retain review reasons without exposing secrets.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow operators to register an LTAC connection using a service location
  and privileged credential supplied through secure operator configuration.
- **FR-002**: System MUST treat LTAC as a sync target named `LTAC`; any accepted connection alias
  MUST still produce plans and artifacts that identify the vendor as `LTAC`.
- **FR-003**: System MUST support LTAC customer scopes as the target entity for N-central customer
  and N-central site sync plans.
- **FR-004**: System MUST generate reviewable sync plans for N-central customer records targeting
  LTAC customer scopes without mutating LTAC during discovery or planning.
- **FR-005**: System MUST generate reviewable sync plans for N-central site records targeting LTAC
  customer scopes while preserving the parent N-central customer identifier.
- **FR-006**: System MUST apply approved N-central customer and site records to LTAC as one
  authoritative batch per reviewed plan, not as independent item-by-item normal sync writes.
- **FR-007**: System MUST never create ad hoc LTAC customer scopes outside the authoritative
  N-central-to-LTAC sync flow.
- **FR-008**: System MUST derive a safe LTAC slug for each synced customer scope and fail the item
  safely with a clear non-secret reason when a safe slug cannot be produced.
- **FR-009**: System MUST include the N-central source identifier for every LTAC customer scope
  created or updated by this feature.
- **FR-010**: System MUST include the parent N-central customer identifier for every N-central site
  synced as an LTAC customer scope.
- **FR-011**: System MUST preserve plan-first safety for every LTAC mutation: inspect, plan, review,
  dry run, then explicit apply.
- **FR-012**: System MUST skip LTAC changes for plan items marked Review, Reject, No Update, or any
  other non-approved decision.
- **FR-013**: System MUST ensure LTAC credentials are never written to exported plans, returned
  connection objects, common error messages, logs, or test fixtures.
- **FR-014**: System MUST never send N-central registration tokens or other unrelated N-central
  secrets to LTAC.
- **FR-015**: System MUST provide clear non-secret operator feedback for connection failures,
  unsafe slug generation, duplicate source identifiers, missing parent identifiers, and LTAC sync
  failures.
- **FR-016**: System MUST keep match and reviewer reasons visible through plan export, import,
  dry-run output, and apply output.

### Key Entities *(include if feature involves data)*

- **LTAC Customer Scope**: A customer scope in LTAC representing either an N-central customer or an
  N-central site, including display name, safe slug, N-central identifier, optional parent N-central
  customer identifier, source-system marker, and active or retired status.
- **N-central Customer Source**: The source customer record whose identifier and name determine the
  LTAC customer scope for customer-level sync.
- **N-central Site Source**: The source site record whose identifier, name, and parent customer
  identifier determine the LTAC customer scope for site-level sync.
- **Plan/Review Artifact**: The reviewable sync artifact containing proposed actions, reviewer
  decisions, source and target evidence, safe-failure reasons, and no credentials.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can generate a reviewable N-central customer-to-LTAC plan and complete a
  dry run without changing LTAC customer scope state.
- **SC-002**: Applying an approved customer sync updates LTAC so 100% of approved N-central customer
  records have matching active LTAC customer scopes.
- **SC-003**: Applying an approved site sync updates LTAC so 100% of approved N-central site records
  have matching LTAC customer scopes with their parent N-central customer identifiers preserved.
- **SC-004**: Credential audits of returned objects, exported plans, dry-run output, apply output,
  and common error paths find zero occurrences of the LTAC privileged credential.
- **SC-005**: Unsafe slugs, missing parent identifiers, and duplicate source identifiers result in
  zero silent guesses and always produce review-blocked or failed items with operator-readable
  reasons.
- **SC-006**: A full approved sync can process at least 1,000 combined customer and site records in
  one operator run while reporting inserted, updated, retired, and active counts to the operator.

## Assumptions

- LTAC remains downstream of N-central for this feature; operators do not manually create synced
  LTAC customer scopes.
- LTAC owns retirement behavior for previously synced N-central records that are absent from a full
  approved sync.
- Existing N-central reads provide customer and site identifiers plus parent customer identifiers
  needed for sync planning.
- LTAC credentials are provided by trusted operators through parameters or environment variables.
- Existing EntitySync plan review, dry-run, and apply semantics remain the operator workflow for
  this feature.
