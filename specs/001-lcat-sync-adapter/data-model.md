# Data Model: LCAT Customer Scope Sync

## LCAT Customer Scope

Represents a customer scope in LCAT materialized from an N-central customer or site.

### Fields

- `vendor`: Constant `LCAT`.
- `entity_type`: Constant `Customer`.
- `id`: Existing LCAT customer-scope identifier when read back from LCAT.
- `display_name`: Human-readable customer or site name shown to operators.
- `slug`: Safe LCAT customer-scope slug.
- `ncentral_customer_id`: N-central identifier for the source customer or source site.
- `ncentral_parent_customer_id`: Parent N-central customer identifier for site-derived scopes;
  empty for customer-derived scopes.
- `source_system`: Constant `ncentral` for synced rows.
- `status`: Active or retired, owned by LCAT sync behavior.

### Validation Rules

- `slug` must match `^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$`.
- `display_name` must be non-empty.
- `ncentral_customer_id` must be non-empty and unique within the batch.
- `ncentral_parent_customer_id` must be present for site-derived scopes.
- Credentials and registration tokens are not fields on this entity and must not be serialized.

### Relationships

- Site-derived LCAT customer scopes reference a parent N-central customer through
  `ncentral_parent_customer_id`.
- Customer-derived LCAT customer scopes have no parent scope requirement.

### State Transitions

- Missing or new source record in approved full sync: not present -> active.
- Existing source record in approved full sync: active -> active with updated fields.
- Previously synced record omitted from approved full sync: active -> retired by LCAT.
- Unsafe or incomplete source record: planned -> review-blocked or failed, no LCAT state change.

## N-central Customer Source

Source customer record used to create or update an LCAT customer scope.

### Fields

- `vendor`: Constant `NCentral`.
- `entity_type`: Constant `Customer`.
- `id`: N-central customer identifier.
- `name`: Display name used for LCAT display name and slug derivation.
- `external_ids`: Optional canonical identifiers, including `NCentralCustomerId` when available.
- `custom_fields`: Optional source metadata. Registration tokens must be ignored.

### Validation Rules

- Effective N-central customer identifier is `NCentralCustomerId` when present, otherwise `id`.
- Effective identifier and display name must be non-empty before apply.
- Source metadata must not include N-central registration tokens in LCAT payloads.

## N-central Site Source

Source site record used to create or update an LCAT customer scope while preserving parent context.

### Fields

- `vendor`: Constant `NCentral`.
- `entity_type`: Constant `Site`.
- `id`: N-central site identifier.
- `name`: Site display name.
- `external_ids`: Includes effective site identifier and parent N-central customer identifier.
- `custom_fields`: Optional parent customer name or display metadata for slug derivation.

### Validation Rules

- Effective N-central site identifier is `NCentralSiteId` when present, otherwise `id`.
- `NCentralCustomerId` must be present before apply.
- Effective identifier and display name must be non-empty before apply.
- Source metadata must not include N-central registration tokens in LCAT payloads.

## Plan/Review Artifact

Reviewable artifact that carries proposed LCAT actions and safety evidence.

### Fields

- `source_vendor`: `NCentral`.
- `source_entity_type`: `Customer` or `Site`.
- `target_vendor`: `LCAT`.
- `target_entity_type`: `Customer`.
- `items`: Proposed customer-scope changes with action, source evidence, optional target evidence,
  score/reasons, and reviewer decision.
- `target_candidates`: Existing LCAT customer scopes when available.

### Validation Rules

- Discovery and planning do not mutate LCAT.
- Only approved write-capable decisions enter the LCAT batch payload.
- Review, reject, no-update, none, unsafe, duplicate, or incomplete items do not mutate LCAT.
- Credentials are excluded from all plan and review artifact fields.
