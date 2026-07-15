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

## SYNTAX

```powershell
Invoke-EntitySyncPlan [-Plan] <EntitySyncPlan> [-Apply] [-PassThru] [-TargetCustomFieldName <String>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

`-Plan` accepts pipeline input by value. `-TargetCustomFieldName` defaults to `CFNetSuiteCustomerID` and is used by HaloPSA target link/update writes.

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

Applies the plan and returns EntityWriteResult objects.

### Example 4
```powershell
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
$results = $plan | Invoke-EntitySyncPlan -Apply -PassThru
```

Dry-runs then applies an N-central Customer to LTAC plan as a single batched customer-scope sync.

## NOTES

HaloPSA to NCentral:

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

NCentral to LTAC:

`New-EntitySyncPlan -SourceVendor NCentral -TargetVendor LTAC` plans apply as one batch request per reviewed plan instead of one write per item. All approved items (Create/Update/Link, mapped to LTAC customer-scope fields `slug`/`display_name`/`ncentral_customer_id`/`ncentral_parent_customer_id`) are sent together in a single call to the AgentController ops/PostgREST OpenAPI RPC at `POST /rpc/sync_ncentral_customers`. `None` items are skipped and `Review` items are written as their own unsuccessful result without joining the batch. `-Apply -WhatIf` reports a single `ShouldProcess` confirmation for the whole batch (`"{count} customer scope(s)"`) and performs no write. With `-Apply -WhatIf -PassThru`, one non-secret preview `EntityWriteResult` per approved batched item is returned and `Raw` holds the mapped LTAC customer-scope request that would be included in the batch. Without `-Apply`, every item is reported as "Planned only" and no batch call is made. With real `-Apply -PassThru`, one `EntityWriteResult` per batched item is returned, each carrying the same aggregate inserted/updated/retired/active counts from the single LTAC response (`Raw` holds the full sync result). See `specs/001-ltac-sync-adapter/contracts/ltac-sync-rpc.md`.

N-central Customer and Site sources batch together in the same call: a customer-derived item carries no `ncentral_parent_customer_id`, while a site-derived item carries the site's own identifier as `ncentral_customer_id` and its parent N-central customer's identifier as `ncentral_parent_customer_id`, preserving the site's parent relationship in LTAC. A site with no parent N-central customer ID never reaches the batch — `New-EntitySyncPlan` blocks it at plan time as `Action 'Review'`, so it is written as its own unsuccessful result the same as any other Review item. Before sending, the adapter rejects the whole batch with a clear error if any two items share the same `ncentral_customer_id`, matching the contract's uniqueness rule.

LTAC apply safety is stricter than the generic item loop: only approved `Create`, `Update`, and `Link` rows with valid customer-scope fields enter the batch. `Review`, `Reject`, `No Update`, `None`, unsafe slug, empty display-name/source-id, missing parent-id, and duplicate source-id rows are skipped or returned as failed non-secret results without being sent to LTAC. The batch payload contains only the mapped customer-scope fields; LTAC bearer tokens, authorization headers, and unrelated N-central registration tokens are not copied into requests, result messages, or exported plan artifacts.
