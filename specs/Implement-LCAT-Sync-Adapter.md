# Implement LCAT Sync Adapter

## Goal

Add `LCAT` as an EntitySync vendor target so N-central customers and sites can be synced into LISSTech Agent Controller / LTAC customer scopes.

Premise: clients are never created manually in LTAC. LTAC customers are materialized only from N-central data through EntitySync.

LTAC-side support is already implemented in `LISSTech.AgentController` commit `87545d8 Add N-central customer sync contract`.

## Scope

Implement in `LISSTech.EntitySync`. Do not extend the N-central adapter unless response-shape handling is required for existing N-central reads.

The expected flow is:

```powershell
Connect-EntitySyncVendor -Vendor NCentral
Connect-EntitySyncVendor -Vendor LCAT

New-EntitySyncPlan `
  -SourceVendor NCentral `
  -SourceEntityType Customer `
  -TargetVendor LCAT `
  -TargetEntityType Customer

Invoke-EntitySyncPlan -Apply
```

Sites should also be supported:

```powershell
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Site -TargetVendor LCAT -TargetEntityType Customer
```

LCAT can store both N-central customers and N-central sites as LTAC customer scopes. For sites, preserve the parent N-central customer ID.

## Vendor Name

Use `LCAT` as the EntitySync vendor name.

Accept `LTAC` only as a connection alias if that is easy, but exported plans should use `LCAT` consistently.

## Connection Surface

Add `Connect-EntitySyncVendor -Vendor LCAT` dynamic parameters:

```powershell
-LCATBaseUrl <string>
-LCATBearerToken <string>
```

Environment variable fallbacks:

```text
LCAT_BASE_URL
LCAT_BEARER_TOKEN
```

`LCATBaseUrl` is the LTAC/PostgREST operator endpoint, for example:

```text
https://ops-agent-controller-prd-ewr-1.clfy-b.lissonline.com
```

`LCATBearerToken` is an LTAC operator JWT accepted by PostgREST. Do not log it, return it, serialize it into plan files, or include it in exception messages.

## Adapter Behavior

Create `src/Adapters/LCAT/LCATOptions.cs` and `src/Adapters/LCAT/LCATEntityAdapter.cs`.

The adapter implements `IEntityAdapter`.

Required behavior:

- `Vendor` returns `LCAT`.
- Supported entity type is `Customer`.
- `GetEntitiesAsync(Customer)` reads active LTAC customer scopes if an API view exists; otherwise return an empty target list so all N-central records plan as create/sync candidates.
- `CreateEntityAsync` and `UpdateEntityAsync` must not create ad hoc LTAC customers independently.
- Actual writes must go through the LTAC sync RPC, not direct table access.

Required implementation detail: batch writes.

The LTAC sync RPC is authoritative for N-central-sourced rows. It retires previously synced N-central rows that are missing from the payload. Therefore EntitySync must not call the RPC with one item at a time unless it is intentionally retiring every other synced row.

Implement one of these approaches:

- Preferred: add an LCAT-specific plan invocation path that sends all approved N-central customer/site plan items in one `sync_ncentral_customers` call.
- Acceptable: make the LCAT adapter buffer approved writes and expose an explicit flush path called once per plan.

Do not use per-item RPC calls for normal sync.

## LTAC RPC Contract

The LTAC side exposes this PostgREST RPC:

```http
POST /rpc/sync_ncentral_customers
Authorization: Bearer <LCAT operator JWT>
Content-Type: application/json
```

Request body:

```json
{
  "p_customers": [
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
  "p_reason": "EntitySync N-central to LCAT sync",
  "p_ticket": null
}
```

Response body:

```json
{
  "inserted_count": 1,
  "updated_count": 1,
  "retired_count": 0,
  "active_count": 2,
  "audit_event_id": "00000000-0000-0000-0000-000000000000"
}
```

The RPC is authoritative for N-central-sourced rows and was added in AgentController commit `87545d8`:

- Upsert by `ncentral_customer_id`.
- Adopt an existing LTAC row by `slug` if that row has no `ncentral_customer_id` yet.
- Update slug/display name/parent/status on changes.
- Set status `active` for rows in the payload.
- Retire previously synced N-central rows missing from a full sync payload.
- Never store N-central registration tokens.

Schema fields added on `customers`:

```text
ncentral_customer_id
ncentral_parent_customer_id
source_system
```

`source_system` is nullable for legacy/bootstrap/test rows, but synced rows use `ncentral`.

## Mapping Rules

From NCentral `Customer` source to LCAT `Customer` request:

```text
slug                        = safe slug derived from source name unless NCentral source already provides one
display_name                = source.Name
ncentral_customer_id        = source.ExternalIds["NCentralCustomerId"] or source.Id
ncentral_parent_customer_id = null
```

From NCentral `Site` source to LCAT `Customer` request:

```text
slug                        = safe slug derived from parent customer name + site name when possible
display_name                = source.Name
ncentral_customer_id        = source.ExternalIds["NCentralSiteId"] or source.Id
ncentral_parent_customer_id = source.ExternalIds["NCentralCustomerId"]
```

Slug rules must match AgentController installer slug safety:

```regex
^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$
```

If slug generation would produce an unsafe or empty slug, fail the item with a clear non-secret message.

## EntitySync Integration Points

Update command vendor validation/completion for `LCAT`:

- `ConnectEntitySyncVendorCommand`
- `GetEntitySyncEntityCommand`
- `NewEntitySyncPlanCommand`
- Any chain/default vendor lists if appropriate

Entity types:

```text
LCAT: Customer
```

Lookups:

```text
LCAT: none for now
```

Mapper updates:

- Extend `DefaultEntityMapper` so NCentral customer/site sources produce LCAT customer write requests with the fields above.
- Do not map secrets.

## Security Requirements

- Never log bearer tokens.
- Never include tokens in plan exports.
- Never send N-central `registrationToken` to LCAT.
- Error messages may include HTTP status and endpoint path, but not authorization headers or request body when it could contain secrets.
- LCAT writes require an existing operator JWT with `operator_access:write`; there is not yet a separate sync scope.

## Tests

Add or update Pester tests to cover:

- `LCAT` appears in `Connect-EntitySyncVendor` vendor validation/completion.
- `LCAT` appears as a target vendor in `New-EntitySyncPlan`.
- `LCAT` supports only `Customer` as an entity type.
- NCentral customer maps to LCAT payload without registration token fields.
- NCentral site maps to LCAT payload with `ncentral_parent_customer_id`.
- `LCATBearerToken` is not emitted in returned objects or common error strings.

Add C# unit-level tests if this repo has a practical C# test harness; otherwise Pester reflection tests are acceptable, matching the existing module style.

## Validation Commands

Run from `LISSTech.EntitySync`:

```powershell
just build
just test-load
just test
```

Manual smoke with real env after connecting NCentral and LCAT:

```powershell
Connect-EntitySyncVendor -Vendor NCentral
Connect-EntitySyncVendor -Vendor LCAT
Get-EntitySyncEntity -Vendor NCentral -Type Customer -Count 3
New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType Customer -TargetVendor LCAT -TargetEntityType Customer -CreateMissing
```

Apply only after reviewing the plan:

```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
$plan | Invoke-EntitySyncPlan -Apply
```
