---
external help file: LISSTech.EntitySync.dll-Help.xml
Module Name: LISSTech.EntitySync
online version:
schema: 2.0.0
---

# New-EntitySyncPlan

## SYNOPSIS
Builds a safe, reviewable synchronization plan between two vendors.

## DESCRIPTION
Retrieves target entities, performs explainable fuzzy matching, and emits an EntitySync plan. This command does not write to any vendor.

When HaloPSA is the target, planning reads full client records by default so custom fields such as `CFNetSuiteCustomerID` are available for link detection. Pass `-FullTargetObjects` only when every HaloPSA client must also be enriched with site details for address/postal matching; this can be slower on large tenants.

`-SourceEntityType` and `-TargetEntityType` are dynamic parameters scoped to the chosen vendors. N-central normally exposes `Customer`/`Site`; when AgentController is the target it also exposes `CustomerScope`, the default complete Customer-plus-Site snapshot. Pipeline input remains available for non-authoritative Customer/Site planning, but is rejected for `CustomerScope` because it cannot prove completeness. `-TargetVendor LTAC` normalizes to `AgentController`.

Bill.com is a first-class vendor. It exposes EntityType `Client` from the Bill Spend & Expense `Client` custom field. When Bill.com is the source and HaloPSA is the target, `-SourceExternalIdName` defaults to `BillSpendClientId` and `-TargetCustomFieldName` defaults to `CFBillSpendClientID` unless explicitly overridden.

Sophos Central exposes partner or organization tenants as EntityType `Customer`. When HaloPSA is the target, `-SourceExternalIdName` defaults to `SophosCentralTenantId` and `-TargetCustomFieldName` defaults to `CFSophosCentralTenantID`. Partner connections can also be targets; organization connections reject writes during apply because Sophos does not expose organization tenant write routes.

Use `-SourceSearch` and optional `-SourceCount` for focused one-off customer plans. They filter the source read before matching, so operators do not have to build a full plan and manually prune unrelated rows.

## SYNTAX

```powershell
New-EntitySyncPlan [-SourceVendor] <HaloPSA|NetSuite|NCentral|Bill.com|'Sophos Central'> [-TargetVendor] <HaloPSA|NetSuite|NCentral|Bill.com|'Sophos Central'|AgentController> [-InputObject <ExternalEntity>] [[-SourceEntityType] <String>] [[-TargetEntityType] <String>] [-IncludeInactive] [-CreateMissing] [-FullTargetObjects] [-SourceSearch <String>] [-SourceCount <Int32>] [-AutoLinkScore <Int32>] [-ReviewScore <Int32>] [-SourceExternalIdName <String>] [-TargetCustomFieldName <String>] [-ThrottleLimit <Int32>] [<CommonParameters>]
```

## EXAMPLES

### Example 1
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -CreateMissing
$plan.Items | Format-Table Action, Score, MatchType, @{n='Source';e={$_.Source.Name}}, @{n='Target';e={$_.Target.Name}}
```

Creates a NetSuite customer to HaloPSA client plan and displays decisions.

### Example 2
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -SourceSearch 'Acme Corp' -SourceCount 1 -CreateMissing
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
```

Builds and dry-runs a one-customer NetSuite to HaloPSA plan.

### Example 3
```powershell
$plan = New-EntitySyncPlan -SourceVendor NetSuite -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client -FullTargetObjects
```

Uses exhaustive HaloPSA client and site enrichment for matching at the cost of additional API calls.

### Example 4
```powershell
$plan = New-EntitySyncPlan -SourceVendor HaloPSA -SourceEntityType Site -TargetVendor NCentral -TargetEntityType Site -CreateMissing
```

Creates a HaloPSA site to N-central site plan. HaloPSA N-central `site_links` are authoritative when present. If a Halo site has no existing site link, the parent Halo client must be linked to an N-central customer so new N-central sites can be created under the correct customer.

### Example 5
```powershell
$plan = New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType CustomerScope -TargetVendor AgentController -TargetEntityType Customer -CreateMissing
```

Creates one authoritative N-central Customer-plus-Site snapshot for AgentController. `CustomerScope` reads both source entity types; `Customer` remains the only AgentController target type. AgentController has no customer-scope read endpoint, so every source plans as `Create`/`NoMatch` with `-CreateMissing`. Customer-derived scopes carry no parent, while site-derived scopes carry `ncentral_parent_customer_id`. See `specs/001-ltac-sync-adapter/contracts/powershell-command-contract.md`.

### Example 6
```powershell
$plan = New-EntitySyncPlan -SourceVendor NCentral -SourceEntityType CustomerScope -TargetVendor AgentController -TargetEntityType Customer -CreateMissing
```

The same `CustomerScope` plan includes every site-derived scope and its parent N-central customer identifier. A source-validation failure becomes `Action 'Review'`, `MatchType 'LtacSourceInvalid'`, and blocks the entire authoritative apply until resolved, preventing an omitted review row from retiring an existing AgentController scope. See `specs/001-ltac-sync-adapter/data-model.md`.

### Example 7
```powershell
Connect-EntitySyncVendor -Vendor Bill.com
Connect-EntitySyncVendor -Vendor HaloPSA
$plan = New-EntitySyncPlan -SourceVendor Bill.com -SourceEntityType Client -TargetVendor HaloPSA -TargetEntityType Client -CreateMissing
```

Creates a Bill.com client to HaloPSA client plan. The default Bill.com-to-HaloPSA link field is `CFBillSpendClientID`.

### Example 8
```powershell
Connect-EntitySyncVendor -Vendor 'Sophos Central'
Connect-EntitySyncVendor -Vendor HaloPSA
$plan = New-EntitySyncPlan -SourceVendor 'Sophos Central' -SourceEntityType Customer -TargetVendor HaloPSA -TargetEntityType Client
```

Creates a Sophos Central tenant to HaloPSA client plan. The default Sophos-to-HaloPSA link field is `CFSophosCentralTenantID`.
