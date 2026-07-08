# Feature Specification: LCAT Customer Scope Sync

**Feature Branch**: `main`

**Created**: 2026-07-08

**Status**: Draft

**Input**: User description: "Implement LCAT Sync Adapter"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sync N-central Customers to LCAT (Priority: P1)

An operator needs to sync N-central customer records into LCAT customer scopes so LCAT reflects the
same customer population managed in N-central without manually creating customers in LCAT.

**Why this priority**: Customer-level sync is the primary business outcome and enables LCAT to use
N-central as the source of truth for customer scopes.

**Independent Test**: Prepare a reviewed plan containing approved N-central customer records, run a
dry run, then apply it and verify LCAT contains active customer scopes for the approved records with
the expected N-central identifiers.

**Acceptance Scenarios**:

1. **Given** approved N-central customer records and an authenticated LCAT connection, **When** the
   operator applies the reviewed sync plan, **Then** LCAT contains active customer scopes matching
   the approved N-central customers.
2. **Given** an N-central customer already synced to LCAT, **When** the customer's display name or
   source identifier is included in a later approved sync, **Then** the existing LCAT customer scope
   is updated rather than duplicated.
3. **Given** an N-central customer is omitted from a full approved sync payload, **When** the sync is
   applied, **Then** the previously synced LCAT customer scope is retired according to LCAT's
   authoritative sync behavior.

---

### User Story 2 - Sync N-central Sites as LCAT Customer Scopes (Priority: P2)

An operator needs N-central site records to appear as LCAT customer scopes while preserving the
relationship to their parent N-central customer.

**Why this priority**: Site-level scope support lets LCAT represent operational customer locations
without losing parent-customer context.

**Independent Test**: Prepare a reviewed plan containing approved N-central site records with parent
customer identifiers, run a dry run, then apply it and verify LCAT customer scopes are created or
updated with the correct parent relationship.

**Acceptance Scenarios**:

1. **Given** an approved N-central site with a known parent customer, **When** the operator applies
   the reviewed sync plan, **Then** LCAT contains a customer scope for the site and records the
   parent N-central customer relationship.
2. **Given** an approved N-central site whose parent customer identifier is missing, **When** the
   operator reviews the plan, **Then** the item is blocked for review or fails safely with a clear
   non-secret reason.

---

### User Story 3 - Protect Credentials and Plan Safety (Priority: P3)

An operator needs to connect LCAT with a privileged credential and run reviewable dry-run/apply flows
without exposing that credential or allowing unreviewed writes.

**Why this priority**: LCAT sync changes customer scope state and uses privileged credentials, so
safety and credential handling are required for production use.

**Independent Test**: Connect to LCAT using configured credentials, generate and export a sync plan,
run a dry run, inspect all returned objects and plan artifacts, and verify the credential is absent
while unapproved or review-blocked items are not applied.

**Acceptance Scenarios**:

1. **Given** an operator provides LCAT credentials, **When** connection, planning, export, dry run,
   or apply output is inspected, **Then** the credential value is not displayed or serialized.
2. **Given** a sync plan contains items marked for review, rejection, or no update, **When** the
   operator applies the plan, **Then** those items are skipped and no LCAT customer scope changes are
   made for them.
3. **Given** the operator runs the plan as a dry run, **When** the run completes, **Then** no LCAT
   customer scope state is changed and the operator can see which approved items would change.

---

### Edge Cases

- LCAT connection credentials are missing, expired, malformed, or unauthorized.
- A source customer or site cannot produce a safe customer-scope slug.
- Two source records would produce the same LCAT slug or N-central identifier.
- A site record lacks its parent N-central customer identifier.
- LCAT is unreachable or returns a partial, failed, or malformed sync result.
- A plan contains no approved N-central customer or site items.
- A full sync intentionally omits previously synced records that LCAT will retire.
- Plan export, import, dry-run, and apply output must retain review reasons without exposing secrets.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow operators to register an LCAT connection using a service location
  and privileged credential supplied through secure operator configuration.
- **FR-002**: System MUST treat LCAT as a sync target named `LCAT`; any accepted connection alias
  MUST still produce plans and artifacts that identify the vendor as `LCAT`.
- **FR-003**: System MUST support LCAT customer scopes as the target entity for N-central customer
  and N-central site sync plans.
- **FR-004**: System MUST generate reviewable sync plans for N-central customer records targeting
  LCAT customer scopes without mutating LCAT during discovery or planning.
- **FR-005**: System MUST generate reviewable sync plans for N-central site records targeting LCAT
  customer scopes while preserving the parent N-central customer identifier.
- **FR-006**: System MUST apply approved N-central customer and site records to LCAT as one
  authoritative batch per reviewed plan, not as independent item-by-item normal sync writes.
- **FR-007**: System MUST never create ad hoc LCAT customer scopes outside the authoritative
  N-central-to-LCAT sync flow.
- **FR-008**: System MUST derive a safe LCAT slug for each synced customer scope and fail the item
  safely with a clear non-secret reason when a safe slug cannot be produced.
- **FR-009**: System MUST include the N-central source identifier for every LCAT customer scope
  created or updated by this feature.
- **FR-010**: System MUST include the parent N-central customer identifier for every N-central site
  synced as an LCAT customer scope.
- **FR-011**: System MUST preserve plan-first safety for every LCAT mutation: inspect, plan, review,
  dry run, then explicit apply.
- **FR-012**: System MUST skip LCAT changes for plan items marked Review, Reject, No Update, or any
  other non-approved decision.
- **FR-013**: System MUST ensure LCAT credentials are never written to exported plans, returned
  connection objects, common error messages, logs, or test fixtures.
- **FR-014**: System MUST never send N-central registration tokens or other unrelated N-central
  secrets to LCAT.
- **FR-015**: System MUST provide clear non-secret operator feedback for connection failures,
  unsafe slug generation, duplicate source identifiers, missing parent identifiers, and LCAT sync
  failures.
- **FR-016**: System MUST keep match and reviewer reasons visible through plan export, import,
  dry-run output, and apply output.

### Key Entities *(include if feature involves data)*

- **LCAT Customer Scope**: A customer scope in LCAT representing either an N-central customer or an
  N-central site, including display name, safe slug, N-central identifier, optional parent N-central
  customer identifier, source-system marker, and active or retired status.
- **N-central Customer Source**: The source customer record whose identifier and name determine the
  LCAT customer scope for customer-level sync.
- **N-central Site Source**: The source site record whose identifier, name, and parent customer
  identifier determine the LCAT customer scope for site-level sync.
- **Plan/Review Artifact**: The reviewable sync artifact containing proposed actions, reviewer
  decisions, source and target evidence, safe-failure reasons, and no credentials.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can generate a reviewable N-central customer-to-LCAT plan and complete a
  dry run without changing LCAT customer scope state.
- **SC-002**: Applying an approved customer sync updates LCAT so 100% of approved N-central customer
  records have matching active LCAT customer scopes.
- **SC-003**: Applying an approved site sync updates LCAT so 100% of approved N-central site records
  have matching LCAT customer scopes with their parent N-central customer identifiers preserved.
- **SC-004**: Credential audits of returned objects, exported plans, dry-run output, apply output,
  and common error paths find zero occurrences of the LCAT privileged credential.
- **SC-005**: Unsafe slugs, missing parent identifiers, and duplicate source identifiers result in
  zero silent guesses and always produce review-blocked or failed items with operator-readable
  reasons.
- **SC-006**: A full approved sync can process at least 1,000 combined customer and site records in
  one operator run while reporting inserted, updated, retired, and active counts to the operator.

## Assumptions

- LCAT remains downstream of N-central for this feature; operators do not manually create synced
  LCAT customer scopes.
- LCAT owns retirement behavior for previously synced N-central records that are absent from a full
  approved sync.
- Existing N-central reads provide customer and site identifiers plus parent customer identifiers
  needed for sync planning.
- LCAT credentials are provided by trusted operators through parameters or environment variables.
- Existing EntitySync plan review, dry-run, and apply semantics remain the operator workflow for
  this feature.
