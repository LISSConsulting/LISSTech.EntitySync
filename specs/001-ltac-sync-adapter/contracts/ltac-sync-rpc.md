# Contract: LTAC N-central Customer Sync

## Purpose

Defines the external LTAC sync contract used when applying an approved N-central customer/site plan.

## Request

- Method: `POST`
- Path: `/rpc/sync_ncentral_customers` on the AgentController ops/PostgREST OpenAPI endpoint
- Authentication: LTAC operator credential in the authorization header
- Content type: `application/json`

```json
{
  "customers": [
    {
      "slug": "Arista-Air-Conditioning",
      "display_name": "Arista Air Conditioning Corp.",
      "ncentral_customer_id": "111",
      "ncentral_parent_customer_id": null
    },
    {
      "slug": "Arista-Air-Conditioning-Main-Office",
      "display_name": "Main Office",
      "ncentral_customer_id": "9001",
      "ncentral_parent_customer_id": "111"
    }
  ],
  "reason": "EntitySync N-central to LTAC sync",
  "ticket": null
}
```

## Request Rules

- `customers` contains all approved N-central customer and site records for the reviewed plan.
- `slug` is required and must match `^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$`.
- `display_name` is required.
- `ncentral_customer_id` is required and must be unique within the request.
- `ncentral_parent_customer_id` is required for site-derived scopes and null for customer-derived
  scopes.
- `reason` identifies the EntitySync run.
- `ticket` is optional and null unless a future operator workflow supplies a change ticket.
- N-central registration tokens and LTAC credentials must never appear in the request body.

## Response

```json
[
  {
    "inserted_count": 1,
    "updated_count": 1,
    "retired_count": 0,
    "active_count": 2,
    "audit_event_id": "00000000-0000-0000-0000-000000000000"
  }
]
```

## Response Rules

- `inserted_count`, `updated_count`, `retired_count`, and `active_count` are reported back to the
  operator when pass-through output is requested.
- `audit_event_id` is preserved in the write result when present.
- PostgREST serializes the SQL `RETURNS TABLE` result as a one-row JSON array for `application/json`.
- Non-success responses must include status and endpoint path in operator-facing errors but must not
  include authorization headers or credential values.

## Batch Semantics

- The request is authoritative for N-central-sourced LTAC customer scopes.
- EntitySync composes one complete snapshot containing both N-central Customer and Site records.
- Any unapproved or invalid plan row blocks the whole request; silently omitting it could retire an
  existing scope.
- Previously synced N-central rows absent from the `customers` payload may be retired by LTAC.
- EntitySync must not use one request per normal plan item.

The typed source for generated client code is `agentcontroller.openapi.json` beside this document.
It projects the live PostgREST Swagger document's untyped `jsonb` argument and unspecified response
into explicit request and one-row response schemas.
