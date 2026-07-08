---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# Invoke-EntitySyncPlan

## SYNOPSIS
Applies a reviewed EntitySync plan.

## DESCRIPTION
Applies create, link, and update actions from a plan. Review items are skipped. The command requires -Apply for writes and supports -WhatIf and -Confirm. Result objects are only written when -PassThru is specified.

## EXAMPLES

### Example 1
```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
```

Shows PowerShell WhatIf messages for the HaloPSA updates and creates that would be made.

### Example 2
```powershell
Import-EntitySyncPlan .\netsuite-halo-client-plan.json | Invoke-EntitySyncPlan -Apply -Confirm
```

Applies a previously reviewed plan with confirmation.

### Example 3
```powershell
$results = $plan | Invoke-EntitySyncPlan -Apply -PassThru
```

## HaloPSA to NCentral

`New-EntitySyncPlan -SourceVendor HaloPSA -TargetVendor NCentral` uses HaloPSA's N-central integration links as authoritative matches during planning. For client plans, if no Halo integration link exists, planning also treats an N-central customer `externalId` equal to the HaloPSA client ID as an owned link marker. For site plans, the parent Halo client must be linked to an N-central customer before creating a missing N-central site.

Apply maintains both sides of the HaloPSA client to N-central customer relationship. Creates use REST, updates use EI2 SOAP `customerModify`, and both write `externalId = <HaloPSA client ID>` plus configured organization custom properties for `HaloPSA Client ID`, `NetSuite Customer ID`, and `NetSuite Customer Name`. After a successful N-central write, HaloPSA `client_links` are upserted with `POST /api/ncentraldetails`.

For HaloPSA Site -> NCentral Site plans, creates use `POST /api/customers/{customerId}/sites`, existing site links update HaloPSA `site_links`, and N-central site field updates are no-op because the confirmed OpenAPI exposes site read/create but no site update endpoint.

Confirmed N-central behavior:

- Customer discovery uses `GET /api/service-orgs/{soId}/customers` when `-NCentralServiceOrgId` is configured, otherwise `GET /api/customers`.
- Customer creation uses `POST /api/service-orgs/{soId}/customers`.
- N-central customer names are sanitized before create by replacing `&` with `and`.
- The OpenAPI spec does not expose a customer update endpoint, so customer updates use EI2 SOAP `customerModify`.
- N-central organization custom properties are updated with EI2 SOAP `organizationPropertyList` and `organizationPropertyModify`.
- HaloPSA N-central client links are written with `POST /api/ncentraldetails` by sending the integration account `id` and updated `client_links` collection.
- N-central site discovery uses `GET /api/sites`; site creation uses `POST /api/customers/{customerId}/sites`.
- HaloPSA N-central site links are written with `POST /api/ncentraldetails` by sending the integration account `id`, preserved `client_links`, and updated `site_links` collection.

Site updates only maintain the HaloPSA integration link until a confirmed N-central site update endpoint is available.

## NCentral to LCAT (planned)

`New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LCAT` plans will apply as one batch request per reviewed plan instead of one write per item. Approved N-central customer and site items will be sent together to `POST /rpc/sync_ncentral_customers`; review, reject, no-update, none, invalid, and incomplete items are skipped. `-WhatIf` will report the planned batch without writing. Pass-through output will report inserted/updated/retired/active counts and an audit event ID; non-success responses will include status and endpoint path without authorization headers or credentials. See `specs/001-lcat-sync-adapter/contracts/lcat-sync-rpc.md`.

Applies the plan and returns EntityWriteResult objects.
