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

Dry-runs then applies a complete N-central `CustomerScope` plan as one authoritative AgentController sync.

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

`New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType CustomerScope -TargetVendor AgentController` reads both N-central Customers and Sites and applies them as one authoritative request through the generated AgentController client. The PostgREST RPC is `POST /rpc/sync_ncentral_customers`; its successful `RETURNS TABLE` payload is a one-row JSON array. `-Apply -WhatIf` reports one `ShouldProcess` confirmation and performs no write. With real `-Apply -PassThru`, each included item carries the aggregate inserted/updated/retired/active counts from that single result.

Customer and Site sources batch together in the same call. A customer-derived item carries no `ncentral_parent_customer_id`; a site-derived item carries its own identifier plus its parent's identifier. Separate Customer/Site plans and pipeline-built `CustomerScope` plans are not valid authoritative snapshots.

AgentController apply blocks the entire `CustomerScope` batch when any `Review`, `Reject`, `No Update`, `None`, unsafe, incomplete, or duplicate row would be omitted. This prevents the authoritative RPC from retiring an existing scope merely because its review row was skipped. Credentials and unrelated N-central registration tokens never enter the generated request or operator-facing errors.
